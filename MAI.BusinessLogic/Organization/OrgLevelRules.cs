namespace MAI.BusinessLogic.Organization
{
    /// <summary>
    /// Alegerea rangului pentru un nivel nou, inserat într-o poziție anume.
    ///
    /// Rangurile au goluri (100, 200, 300), ca un nivel nou să poată fi pus
    /// între două existente fără renumerotarea subdiviziunilor deja create.
    /// </summary>
    public static class OrgLevelRules
    {
        public const int MinRank = 1;
        public const int MaxRank = 10_000;
        public const int Step = 100;

        /// <summary>
        /// Rangul pentru un nivel inserat imediat sub <paramref name="afterRank"/>
        /// (null = deasupra tuturor). Null dacă între cele două niveluri vecine nu
        /// mai există niciun rang liber.
        /// </summary>
        public static int? RankAfter(IReadOnlyCollection<int> existing, int? afterRank)
        {
            var sorted = existing.Distinct().OrderBy(r => r).ToList();

            int lower = afterRank ?? 0;
            if (afterRank.HasValue && !sorted.Contains(afterRank.Value)) return null;

            var next = sorted.Where(r => r > lower).Cast<int?>().FirstOrDefault();

            if (next is null)
            {
                var rank = lower + Step;
                return rank <= MaxRank ? rank : null;
            }

            if (next.Value - lower < 2) return null;
            return lower + (next.Value - lower) / 2;
        }
    }
}
