namespace MAI.Domain.Enums
{
    /// <summary>Ciclul de viață al unui document intern. Persistat ca int.</summary>
    public enum InternalDocumentStatus
    {
        /// <summary>Editabil, vizibil doar autorului. Distribuția nu e încă stabilită.</summary>
        Draft = 0,

        /// <summary>
        /// Distribuit. Lista destinatarilor a fost fixată în momentul publicării
        /// și nu se mai schimbă — este lista față de care se măsoară „luat la
        /// cunoștință”.
        /// </summary>
        Published = 1,

        /// <summary>
        /// Abrogat: rămâne vizibil (cu mențiunea abrogării) ca istoric, dar nu mai
        /// cere confirmare de luare la cunoștință.
        /// </summary>
        Repealed = 2,
    }

    /// <summary>
    /// Cui se distribuie un document intern. Verificarea se face pe server
    /// (DistributionResolver): interfața afișează doar opțiunile permise, dar
    /// nu e ea garanția.
    /// </summary>
    public enum DistributionMode
    {
        /// <summary>Subdiviziunea condusă de autor, cu toate subunitățile ei.</summary>
        MyUnitTree = 0,

        /// <summary>
        /// Doar subordonații direcți ai autorului: membrii subdiviziunii conduse
        /// (fără subunități) și șefii subunităților imediat inferioare.
        /// </summary>
        DirectSubordinates = 1,

        /// <summary>Subdiviziuni alese explicit (opțional cu subunitățile lor).</summary>
        SelectedUnits = 2,

        /// <summary>Doar șefii subunităților din subordinea autorului.</summary>
        UnitHeads = 3,

        /// <summary>Persoane alese nominal.</summary>
        SpecificUsers = 4,

        /// <summary>Toți utilizatorii activi. Doar Administrator.</summary>
        WholeInstitution = 5,
    }

    /// <summary>Tipul unei ținte de distribuție memorate pe document.</summary>
    public enum DistributionTargetKind
    {
        Unit = 0,
        User = 1,
    }
}
