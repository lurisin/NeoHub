namespace DSC.TLink.ITv2.Enumerations
{
    /// <summary>
    /// The alarm raised by <see cref="Messages.ActivateFap"/>, as the F / A / P keys of a keypad do. Values from
    /// LawPaul/HA_DSC_Neo_ITv2 (GPL-3.0), dsc_itv2/messages.py ModuleControlActivateFAP ("per panel manual", no manual
    /// named); not verified on a panel. There is deliberately no default: callers always name the type.
    /// </summary>
    public enum FapType : byte
    {
        Fire = 0,
        Auxiliary = 1,
        Panic = 2,
    }
}
