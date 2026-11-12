using System.Collections.Generic;

namespace OpenPedalControl.Hid;

/// <summary>Something that provides live raw axis values: a real device, or a fake one in tests.</summary>
public interface IAxisSource
{
    IReadOnlyList<HidAxis> Axes { get; }

    /// <summary>Latest raw value of an axis, or false while it has not been reported yet.</summary>
    bool TryGetValue(int axisIndex, out int value);
}
