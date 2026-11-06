using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

namespace Sanity.Symptoms;

// Where a phantom can be: shared by every symptom that places something in the world (phantom sound, phantom steps).
// Behind or beside you (never where you're looking), on walkable floor (the monsters' own navigation mesh), at about
// your height.
internal static class PhantomPlacement
{
    // At least this far (degrees) from where you're looking.
    internal const float BehindMinAngle = 100f;

    // Where the player is looking, along the ground (falls back to where the body faces).
    internal static Vector3 LookDirection(PlayerControllerB player)
    {
        Vector3 forward = Flat(player.gameplayCamera.transform.forward);
        return forward != Vector3.zero ? forward : Flat(player.transform.forward);
    }

    // A random ground direction behind or beside you: BehindMinAngle to 360 - BehindMinAngle from where you look.
    internal static Vector3 RandomBehindDirection(Vector3 lookDirection)
    {
        float yaw = Random.Range(BehindMinAngle, 360f - BehindMinAngle);
        return Quaternion.AngleAxis(yaw, Vector3.up) * lookDirection;
    }

    // Snap a point to walkable floor; reject it if it's on another floor or ended up too close.
    // floor: the point on the floor itself (callers lift it to where the sound comes from).
    internal static bool TrySample(Vector3 origin, Vector3 point, float heightTolerance, float minFlatDistance,
                                   out Vector3 floor)
    {
        floor = Vector3.zero;
        if (!NavMesh.SamplePosition(point, out NavMeshHit hit, 3f, NavMesh.AllAreas))
        {
            return false;
        }

        if (Mathf.Abs(hit.position.y - origin.y) > heightTolerance
            || FlatDistance(origin, hit.position) < minFlatDistance)
        {
            return false;
        }

        floor = hit.position;
        return true;
    }

    // How far you can walk in a straight line from a floor point toward a direction, up to maxDistance
    // (stops at walls, drops and gaps in the floor).
    internal static float ClearWalk(Vector3 floor, Vector3 direction, float maxDistance)
    {
        Vector3 end = floor + direction * maxDistance;
        return NavMesh.Raycast(floor, end, out NavMeshHit hit, NavMesh.AllAreas) ? hit.distance : maxDistance;
    }

    // Your own spot on the floor (a point exactly under you can miss the mesh by a little).
    internal static bool TryFloorUnder(PlayerControllerB player, out Vector3 floor)
    {
        floor = Vector3.zero;
        if (!NavMesh.SamplePosition(player.transform.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
        {
            return false;
        }
        floor = hit.position;
        return true;
    }

    // Distance along the ground, ignoring height.
    internal static float FlatDistance(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        d.y = 0f;
        return d.magnitude;
    }

    // A ground-level DIRECTION (length 1). Not for distances: use FlatDistance for that.
    internal static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.zero;
    }

    // Angle (degrees, along the ground) between where you're looking and a point: 0 = straight ahead, 180 = behind.
    internal static float AngleFromLook(PlayerControllerB player, Vector3 position)
    {
        Vector3 toPoint = Flat(position - player.transform.position);
        return toPoint == Vector3.zero ? 0f : Vector3.Angle(LookDirection(player), toPoint);
    }

    // For telemetry: roughly where a point is, relative to where you were looking.
    internal static string Side(PlayerControllerB player, Vector3 position)
    {
        float angle = Vector3.SignedAngle(Flat(player.gameplayCamera.transform.forward),
                                          Flat(position - player.transform.position), Vector3.up);
        if (Mathf.Abs(angle) > 150f)
        {
            return "behind";
        }
        return angle > 0f ? "right" : "left";
    }
}
