using System.Globalization;

namespace CleanSuite.Safety;

/// <summary>
/// Time stamp in the names of every backup ("Jnk …zip", "Dup …zip", "Reg ….reg", "Shortcuts …\", "Startup …\"): the
/// original program's scheme, so its backups and CleanSuite's sort and list together on the Restore page. Always with
/// the invariant culture: Arabic digits or another AM/PM designator in a file name would not be found again.
/// </summary>
public static class BackupNames
{
    public const string StampFormat = "yyyy_MM_dd_hh_mm_ss_tt";

    public static string Timestamp(DateTime? at = null) => (at ?? DateTime.Now).ToString(StampFormat, CultureInfo.InvariantCulture);
}
