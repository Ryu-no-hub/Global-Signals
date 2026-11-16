using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Shared.Logging;
using SpaceEngineers.Game.Entities.Blocks;   // <-- MyLandingGear
using VRage.Game.Components;
using VRage.ModAPI;
using VRageMath;

namespace Shared.Patches
{
    /// <summary>
    /// Ловит крутящий момент / угловую скорость на гриде в первые N секунд после
    /// расстыковки (коннектор, landing gear) и ОБРЕЗАЕТ аномальные значения.
    ///
    /// Силу (force) в AddForce не трогаем — иначе пропадёт тяга/гравитация.
    /// </summary>
    public static class ConnectorUndockDiagnostics
    {
        public static bool Enabled = true;

        private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

        public static float MaxAllowedTorque = 1000f;
        public static float MaxAllowedAngularVelocity = 5f;

        private static readonly TimeSpan BlockLogCooldown = TimeSpan.FromMilliseconds(500);

        private static readonly ConcurrentDictionary<long, DateTime> RecentlyUndocked
            = new ConcurrentDictionary<long, DateTime>();

        private static readonly ConcurrentDictionary<long, DateTime> _lastBlockLog
            = new ConcurrentDictionary<long, DateTime>();

        private static bool _physicsPatched;

        private static IPluginLogger Log => global::TorchPlugin.Plugin.Instance?.Log;

        public static void Install(Harmony harmony)
        {
            // ---- Ship connector: Detach(bool) — CLAMP ----
            try
            {
                var detach = AccessTools.Method(typeof(MyShipConnector), "Detach", new[] { typeof(bool) });
                if (detach != null && !detach.IsAbstract)
                {
                    harmony.Patch(detach,
                        prefix: new HarmonyMethod(typeof(ConnectorUndockDiagnostics), nameof(BeforeDetach)));
                    Log?.Info($"[UndockDiag] Patched {detach.DeclaringType?.FullName}::{detach.Name}");
                }
                else
                {
                    Log?.Warning($"[UndockDiag] MyShipConnector.Detach(bool) not found " +
                                 $"(found={detach != null}, abstract={detach?.IsAbstract})");
                }
            }
            catch (Exception e)
            {
                Log?.Error(e, "[UndockDiag] Failed to patch MyShipConnector.Detach — connector diagnostics disabled.");
            }

            // ---- Landing gear: MyLandingGear.Detach — CLAMP ----
            try
            {
                var detach = AccessTools.Method(typeof(MyLandingGear), "Detach", Type.EmptyTypes)
                             ?? AccessTools.Method(typeof(MyLandingGear), "Detach", new[] { typeof(bool) });

                if (detach != null && !detach.IsAbstract)
                {
                    harmony.Patch(detach,
                        prefix: new HarmonyMethod(typeof(ConnectorUndockDiagnostics), nameof(BeforeLandingGearRelease)));
                    Log?.Info($"[UndockDiag] Patched {detach.DeclaringType?.FullName}::{detach.Name}");
                }
                else
                {
                    Log?.Warning($"[UndockDiag] MyLandingGear.Detach not found " +
                                 $"(found={detach != null}, abstract={detach?.IsAbstract})");
                }
            }
            catch (Exception e)
            {
                Log?.Error(e, "[UndockDiag] Failed to patch MyLandingGear.Detach — landing gear diagnostics disabled.");
            }

            // Физику патчим отложенно, при появлении первого реального грида.
            MyEntities.OnEntityAdd += OnEntityAddForPhysicsHook;
        }

        // ====================================================================
        // Prefixes
        // ====================================================================

        // __0 = первый (и единственный) параметр Detach(bool), независимо от его реального имени.
        private static void BeforeDetach(MyShipConnector __instance, bool __0)
        {
            if (!Enabled) return;
            try
            {
                MarkGrid(__instance?.CubeGrid as MyCubeGrid);

                var other = __instance?.Other as MyShipConnector;
                if (other != null)
                    MarkGrid(other.CubeGrid as MyCubeGrid);
            }
            catch (Exception e)
            {
                Log?.Error(e, "[UndockDiag] BeforeDetach failed");
            }
        }

        // MyLandingGear.Detach() — вызывается при отстыковке/отсоединении.
        private static void BeforeLandingGearRelease(MyLandingGear __instance)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                var selfGrid = __instance.CubeGrid as MyCubeGrid;
                MarkGrid(selfGrid);
                MarkAttachedGrid(__instance, selfGrid,
                    "AttachedEntity", "LockedTo", "AttachedBlock", "Other", "OtherBlock");
            }
            catch (Exception e)
            {
                Log?.Error(e, "[UndockDiag] BeforeLandingGearRelease failed");
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        /// <summary>
        /// Через рефлексию достаёт «соседний» грид у landing gear по типичным
        /// именам свойств (имя зависит от ревизии игры).
        /// </summary>
        private static void MarkAttachedGrid(object instance, MyCubeGrid selfGrid,
            params string[] propertyNames)
        {
            if (instance == null) return;

            var type = instance.GetType();
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var name in propertyNames)
            {
                PropertyInfo prop;
                try { prop = type.GetProperty(name, flags); }
                catch { continue; }
                if (prop == null) continue;

                object value;
                try { value = prop.GetValue(instance); }
                catch { continue; }

                var grid = ExtractGrid(value);
                if (grid != null && !ReferenceEquals(grid, selfGrid))
                    MarkGrid(grid);
            }
        }

        private static MyCubeGrid ExtractGrid(object value)
        {
            if (value == null) return null;
            if (value is MyCubeGrid g) return g;
            if (value is MyCubeBlock block) return block.CubeGrid as MyCubeGrid;
            if (value is IMyEntity ent) return ent as MyCubeGrid;
            return null;
        }

        /// <summary>
        /// Размечать грид имеет смысл только когда сессия реально работает
        /// (не в процессе Load/Unload, когда игра массово дёргает Detach
        /// у коннекторов и лендинг-гиров по всему миру).
        /// </summary>
        private static bool SessionIsRunning()
        {
            var session = MySession.Static;
            if (session == null) return false;
            if (!session.Ready) return false;   // false во время Load / Unload
            return true;
        }

        private static void MarkGrid(MyCubeGrid grid)
        {
            if (grid == null) return;

            // Грид уже мёртв / вне сцены / без физики — ничего не размечаем.
            if (grid.Closed || grid.MarkedForClose) return;
            if (!grid.InScene) return;
            if (grid.Physics == null) return;

            // Сессия ещё грузится / уже выгружается — Detach'и идут пачкой по всему миру,
            // это не настоящие отстыковки, пропускаем.
            if (!SessionIsRunning()) return;

            RecentlyUndocked[grid.EntityId] = DateTime.UtcNow;
            Log?.Info($"[UndockDiag] Undock: grid '{grid.DisplayName}' ({grid.EntityId}) " +
                      $"marked as suspect for {Window.TotalSeconds:0}s");
        }

        private static bool IsSuspect(long gridEntityId, out TimeSpan age)
        {
            age = TimeSpan.Zero;
            if (!RecentlyUndocked.TryGetValue(gridEntityId, out var t))
                return false;

            age = DateTime.UtcNow - t;
            if (age > Window)
            {
                RecentlyUndocked.TryRemove(gridEntityId, out _);
                return false;
            }
            return true;
        }

        // ====================================================================
        // Physics hook (без изменений)
        // ====================================================================

        private static void OnEntityAddForPhysicsHook(IMyEntity entity)
        {
            if (_physicsPatched) return;
            if (!(entity is MyCubeGrid grid) || grid.Physics == null) return;

            try
            {
                var harmony = new Harmony("Global Mechanics plugin.UndockDiag");

                var addForce = AccessTools.Method(typeof(MyPhysicsBody), "AddForce", new[]
                {
                    typeof(MyPhysicsForceType),
                    typeof(Vector3?),
                    typeof(Vector3D?),
                    typeof(Vector3?),
                    typeof(float?),
                    typeof(bool),
                    typeof(bool),
                });

                if (addForce != null && !addForce.IsAbstract)
                {
                    harmony.Patch(addForce,
                        prefix: new HarmonyMethod(typeof(ConnectorUndockDiagnostics), nameof(BeforeAddForce)));
                    Log?.Info($"[UndockDiag] Patched {addForce.DeclaringType?.FullName}::AddForce(7 args)");
                }
                else
                {
                    Log?.Warning($"[UndockDiag] MyPhysicsBody.AddForce(7 args) not found or is abstract " +
                                 $"(found={addForce != null}, abstract={addForce?.IsAbstract})");
                }

                var angVelSetter = AccessTools.PropertySetter(typeof(MyPhysicsBody), "AngularVelocity")
                                   ?? AccessTools.PropertySetter(typeof(MyPhysicsComponentBase), "AngularVelocity");
                if (angVelSetter != null && !angVelSetter.IsAbstract)
                {
                    harmony.Patch(angVelSetter,
                        prefix: new HarmonyMethod(typeof(ConnectorUndockDiagnostics), nameof(BeforeSetAngularVelocity)));
                    Log?.Info($"[UndockDiag] Patched {angVelSetter.DeclaringType?.FullName}::set_AngularVelocity");
                }
                else
                {
                    Log?.Warning($"[UndockDiag] set_AngularVelocity not found or is abstract " +
                                 $"(found={angVelSetter != null}, abstract={angVelSetter?.IsAbstract})");
                }

                _physicsPatched = true;
            }
            catch (Exception e)
            {
                Log?.Error(e, "[UndockDiag] Failed to attach physics diagnostics");
            }
            finally
            {
                MyEntities.OnEntityAdd -= OnEntityAddForPhysicsHook;
            }
        }

        /// <summary>
        /// Prefix для AddForce. torque по ref — значение реально подменяется
        /// перед вызовом оригинала. force не трогаем.
        /// </summary>
        private static void BeforeAddForce(
            MyPhysicsComponentBase __instance,
            MyPhysicsForceType type,
            Vector3? force,
            Vector3D? position,
            ref Vector3? torque,
            float? maxSpeed,
            bool applyImmediately,
            bool activeOnly)
        {
            if (!Enabled || torque == null) return;

            var grid = __instance?.Entity as MyCubeGrid;
            if (grid == null) return;
            if (!IsSuspect(grid.EntityId, out var age)) return;

            double torqueMag = torque.Value.Length();
            if (torqueMag <= MaxAllowedTorque) return;

            var original = torque.Value;
            torque = ClampMagnitude(original, MaxAllowedTorque);

            LogClamp(grid, "AddForce.torque", age, original.Length(), torque.Value.Length());
        }

        /// <summary>
        /// Prefix для set_AngularVelocity. value по ref — клэмпится по длине,
        /// направление сохраняется. Присвоение в оригинал всегда происходит.
        /// </summary>
        private static void BeforeSetAngularVelocity(MyPhysicsComponentBase __instance, ref Vector3 value)
        {
            if (!Enabled) return;

            var grid = __instance?.Entity as MyCubeGrid;
            if (grid == null) return;
            if (!IsSuspect(grid.EntityId, out var age)) return;

            double mag = value.Length();
            if (mag <= MaxAllowedAngularVelocity) return;

            var original = value;
            value = ClampMagnitude(value, MaxAllowedAngularVelocity);

            LogClamp(grid, "AngularVelocity", age, original.Length(), value.Length());
        }

        private static Vector3 ClampMagnitude(Vector3 v, float maxLength)
        {
            double len = v.Length();
            if (len <= maxLength || len < 1e-6) return v;
            return v * (float)(maxLength / len);
        }

        private static void LogClamp(MyCubeGrid grid, string tag, TimeSpan age, double before, double after)
        {
            var now = DateTime.UtcNow;

            if (_lastBlockLog.TryGetValue(grid.EntityId, out var last)
                && (now - last) < BlockLogCooldown)
                return;

            _lastBlockLog[grid.EntityId] = now;

            Log?.Warning(
                $"[UndockDiag] CLAMPED {tag} on grid '{grid.DisplayName}' ({grid.EntityId}) " +
                $"{age.TotalMilliseconds:0}ms after undock. {before:0.00} -> {after:0.00}");
        }
    }
}