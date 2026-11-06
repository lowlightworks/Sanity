using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Sanity;

// The game's own input actions ("Interact", "PingScan"), read for telemetry only: did you try to use or scan
// something that a symptom faked. Never changes or consumes any input.
internal static class InputWatch
{
    private static readonly Dictionary<string, InputAction> Actions = new Dictionary<string, InputAction>();

    internal static bool Pressed(string name)
    {
        try
        {
            if (!Actions.TryGetValue(name, out InputAction action) || action == null)
            {
                action = InputSystem.actions?.FindAction(name)!;
                if (action == null)
                {
                    return false;   // not set up yet: look again next time
                }
                Actions[name] = action;
            }
            return action.WasPressedThisFrame();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
