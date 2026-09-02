using UnityEngine;

namespace CupkekGames.Recording
{
  /// <summary>
  /// Seam for hour-sweep recordings: the consuming project's time-of-day
  /// system, registered once per domain load via
  /// <see cref="RecorderTool.WorldClock"/> from an
  /// <c>[InitializeOnLoadMethod]</c>. Hour params on the Record* APIs are
  /// only honored when a clock is registered; requesting a sweep without
  /// one fails loudly.
  /// </summary>
  public interface IRecorderWorldClock
  {
    /// <summary>
    /// Acquire the clock for a session and start a runtime hold that
    /// overrides normal ticking. Return false when no clock exists in the
    /// current project/scene (the session then fails loudly).
    /// </summary>
    bool TryBeginHold();

    /// <summary>Release the hold taken by <see cref="TryBeginHold"/>.</summary>
    void EndHold();

    /// <summary>Set the world hour (0..24) for the current frame.</summary>
    void SetHour(float hour, bool snap);

    /// <summary>Direction sunlight travels (used to face the sun at a given hour).</summary>
    Vector3 SunDirection { get; }
  }
}
