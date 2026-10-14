using Keyloom.Models;

namespace Keyloom.Recording;

public sealed record RecordingOptions(
    bool MouseMovement = true, bool MouseButtons = true, bool Keyboard = true,
    bool Timing = true, bool Append = true, int MovementIntervalMs = 16,
    CoordinateSpace Space = CoordinateSpace.Screen, bool Simplify = true);
