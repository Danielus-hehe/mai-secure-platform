namespace MAI.Domain.Enums
{
    public enum TransferCategory
    {
        /// <summary>Critic - necesită atenție imediată.</summary>
        Critical = 0,

        /// <summary>Important - prioritate ridicată.</summary>
        Important = 1,

        /// <summary>General - implicit, uz comun.</summary>
        General = 2,

        /// <summary>Obișnuit - prioritate scăzută.</summary>
        Normal = 3,
    }
}