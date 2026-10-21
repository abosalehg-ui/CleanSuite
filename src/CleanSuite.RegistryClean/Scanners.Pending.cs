namespace CleanSuite.RegistryClean;

public sealed partial class RegistryScanner
{
    /// <summary>
    /// ActiveX &amp; COM (0x40) is not implemented: extracting its specification was stopped by the
    /// automated safety system, and by the user's instruction that part was not regenerated. The category is accepted but
    /// reports nothing. (CLSID existence checks used by the other categories are implemented.)
    /// </summary>
    private void ScanActiveX(RegCategory cat) { }
}
