using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Components;
using VRageMath;

namespace TorchPlugin
{
    /// <summary>
    /// Skips the per-frame work of MyAdvancedDoor.UpdateDoorPosition for doors that are
    /// fully idle: no pending state change, every opening sequence at its limit, on a grid
    /// whose physics body and linear/angular velocities are unchanged since the last full
    /// update. The original method synchronizes all subpart physics velocities with the
    /// parent grid every frame even for fully open/closed hangar doors, which is a fixed
    /// per-frame cost (e.g. 48 X-LargeAirtightHangarDoor gates with 16 subparts each on a
    /// single station grid).
    ///
    /// The original method still runs when the feature is disabled, on first observation of
    /// a door, for forced updates (simplicicationOverride), during state transitions or
    /// animation, after grid/physics changes, after any velocity change, and at least once
    /// every ForceUpdateIntervalTicks as a drift/resync safeguard.
    /// </summary>
    [HarmonyPatch(typeof(MyAdvancedDoor), "UpdateDoorPosition")]
    public static class AdvancedDoorIdleOptimizationPatch
    {
        private const int ForceUpdateIntervalTicks = 60;

        private sealed class DoorState
        {
            internal MyCubeGrid Grid;
            internal MyPhysicsComponentBase GridPhysics;
            internal Vector3 LinearVelocity;
            internal Vector3 AngularVelocity;
            internal long LastUpdateTick;
        }

        // Keys are weak references: entries disappear together with the door entity,
        // no explicit cleanup is needed when grids or doors are closed.
        private static readonly ConditionalWeakTable<MyAdvancedDoor, DoorState> States
            = new ConditionalWeakTable<MyAdvancedDoor, DoorState>();

        private static Func<MyAdvancedDoor, bool> _stateChangeGetter;
        private static Func<MyAdvancedDoor, List<bool>> _currentlyAtLimitGetter;

        // Harmony calls this before applying the patch. Returning false skips patching
        // (for example after a game update renamed the private fields) without failing
        // the rest of PatchAll.
        public static bool Prepare()
        {
            try
            {
                FieldInfo stateChangeField = AccessTools.Field(typeof(MyAdvancedDoor), "m_stateChange");
                FieldInfo atLimitField = AccessTools.Field(typeof(MyAdvancedDoor), "m_currentlyAtLimit");
                if (stateChangeField == null || stateChangeField.FieldType != typeof(bool)
                    || atLimitField == null || atLimitField.FieldType != typeof(List<bool>))
                {
                    Plugin.Instance?.Log.Warning("AdvancedDoorIdleOptimizationPatch: game fields not found, patch disabled");
                    return false;
                }

                ParameterExpression door = Expression.Parameter(typeof(MyAdvancedDoor), "door");
                _stateChangeGetter = Expression.Lambda<Func<MyAdvancedDoor, bool>>(
                    Expression.Field(door, stateChangeField), door).Compile();
                _currentlyAtLimitGetter = Expression.Lambda<Func<MyAdvancedDoor, List<bool>>>(
                    Expression.Field(door, atLimitField), door).Compile();

                Plugin.Instance?.Log.Info("AdvancedDoorIdleOptimizationPatch ready (controlled by OptimizeAdvancedDoors config, default off)");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Log.Critical(ex, "AdvancedDoorIdleOptimizationPatch.Prepare failed");
                return false;
            }
        }

        [HarmonyPrefix]
        public static bool UpdateDoorPositionPrefix(MyAdvancedDoor __instance, bool simplicicationOverride)
        {
            // Forced updates (init, enable/close changes) must never be skipped.
            if (simplicicationOverride)
                return true;

            Plugin plugin = Plugin.Instance;
            if (plugin == null)
                return true;

            var config = plugin.Config as PluginConfig;
            if (config == null || !config.OptimizeAdvancedDoors)
                return true;

            MyCubeGrid grid = __instance.CubeGrid;
            MyPhysicsComponentBase gridPhysics = grid?.Physics;
            if (gridPhysics == null)
                return true; // the original method handles null grid physics itself

            long tick = plugin.Tick;

            if (!States.TryGetValue(__instance, out DoorState state))
            {
                // First observation: run the original and record the reference state.
                States.Add(__instance, Capture(grid, gridPhysics, tick));
                return true;
            }

            // Run the original (and refresh the reference state) whenever anything
            // relevant changed, or periodically as a drift/resync safeguard.
            if (_stateChangeGetter(__instance)
                || !ReferenceEquals(state.Grid, grid)
                || !ReferenceEquals(state.GridPhysics, gridPhysics)
                || gridPhysics.LinearVelocity != state.LinearVelocity
                || gridPhysics.AngularVelocity != state.AngularVelocity
                || tick - state.LastUpdateTick >= ForceUpdateIntervalTicks
                || AnySequenceNotAtLimit(_currentlyAtLimitGetter(__instance)))
            {
                Refresh(state, grid, gridPhysics, tick);
                return true;
            }

            // Idle door on an unchanged grid: subpart velocities are already synchronized
            // with the unchanged grid velocity and no animation or transition is pending.
            return false;
        }

        private static bool AnySequenceNotAtLimit(List<bool> currentlyAtLimit)
        {
            if (currentlyAtLimit == null)
                return true; // no animation state available: do not skip

            for (int i = 0; i < currentlyAtLimit.Count; i++)
                if (!currentlyAtLimit[i])
                    return true;

            return false;
        }

        private static DoorState Capture(MyCubeGrid grid, MyPhysicsComponentBase gridPhysics, long tick)
        {
            var state = new DoorState();
            Refresh(state, grid, gridPhysics, tick);
            return state;
        }

        private static void Refresh(DoorState state, MyCubeGrid grid, MyPhysicsComponentBase gridPhysics, long tick)
        {
            state.Grid = grid;
            state.GridPhysics = gridPhysics;
            state.LinearVelocity = gridPhysics.LinearVelocity;
            state.AngularVelocity = gridPhysics.AngularVelocity;
            state.LastUpdateTick = tick;
        }
    }
}
