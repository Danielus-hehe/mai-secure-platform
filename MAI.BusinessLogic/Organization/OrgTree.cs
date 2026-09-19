using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Organization
{
    /// <summary>Nodul minim al arborelui - fără EF, ca regulile să se poată testa.</summary>
    public sealed record OrgUnitNode(Guid Id, Guid? ParentId, Guid? HeadUserId, OrgUnitType Type, bool IsActive, string Name = "");

    /// <summary>
    /// Arborele subdiviziunilor, construit în memorie dintr-o singură interogare.
    ///
    /// O instituție are sute de subdiviziuni, nu milioane: e mai simplu și mai
    /// sigur să citim toată structura o dată și să o parcurgem în C# decât să
    /// scriem CTE-uri recursive în SQL pentru fiecare întrebare („cine e sub
    /// mine?”, „e X în subordinea lui Y?”).
    /// </summary>
    public sealed class OrgTree
    {
        private readonly Dictionary<Guid, OrgUnitNode> _byId;
        private readonly Dictionary<Guid, List<OrgUnitNode>> _children;

        public OrgTree(IEnumerable<OrgUnitNode> units)
        {
            _byId = units.ToDictionary(u => u.Id);
            _children = _byId.Values
                .Where(u => u.ParentId.HasValue && _byId.ContainsKey(u.ParentId.Value))
                .GroupBy(u => u.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        public IReadOnlyCollection<OrgUnitNode> All => _byId.Values;

        public OrgUnitNode? Find(Guid id) => _byId.GetValueOrDefault(id);

        public IReadOnlyList<OrgUnitNode> ChildrenOf(Guid id) =>
            _children.TryGetValue(id, out var list) ? list : [];

        /// <summary>
        /// Subdiviziunea și toți descendenții ei (în lățime). Protejat împotriva
        /// ciclurilor: dacă datele din bază ar conține unul (modificare manuală),
        /// parcurgerea se oprește în loc să ruleze la infinit.
        /// </summary>
        public IReadOnlyList<OrgUnitNode> Subtree(Guid rootId, bool includeRoot = true)
        {
            var result = new List<OrgUnitNode>();
            if (!_byId.TryGetValue(rootId, out var root)) return result;

            var seen  = new HashSet<Guid> { rootId };
            var queue = new Queue<OrgUnitNode>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (includeRoot || node.Id != rootId) result.Add(node);

                foreach (var child in ChildrenOf(node.Id))
                    if (seen.Add(child.Id)) queue.Enqueue(child);
            }

            return result;
        }

        /// <summary>True dacă <paramref name="unitId"/> e <paramref name="ancestorId"/> sau un descendent al lui.</summary>
        public bool IsInSubtree(Guid unitId, Guid ancestorId)
        {
            var seen = new HashSet<Guid>();
            Guid? current = unitId;
            while (current.HasValue && seen.Add(current.Value))
            {
                if (current.Value == ancestorId) return true;
                current = _byId.TryGetValue(current.Value, out var n) ? n.ParentId : null;
            }
            return false;
        }

        /// <summary>Calea de la vârf până la subdiviziune: „DGP / Secția X / Serviciul Y”.</summary>
        public string PathOf(Guid unitId, string separator = " / ")
        {
            var names = new List<string>();
            var seen  = new HashSet<Guid>();
            Guid? current = unitId;
            while (current.HasValue && seen.Add(current.Value) && _byId.TryGetValue(current.Value, out var n))
            {
                names.Add(n.Name);
                current = n.ParentId;
            }
            names.Reverse();
            return string.Join(separator, names);
        }

        /// <summary>Subdiviziunea condusă de utilizator, dacă există (cel mult una).</summary>
        public OrgUnitNode? LedBy(Guid userId) =>
            _byId.Values.FirstOrDefault(u => u.HeadUserId == userId && u.IsActive);

        /// <summary>
        /// Verifică dacă o subdiviziune poate sta sub părintele propus:
        /// fără cicluri, iar rangul nivelului copilului e strict mai mare decât al
        /// părintelui. Nivelurile pot fi sărite (un Serviciu direct sub o
        /// Direcție e permis), dar nu inversate.
        /// </summary>
        /// <returns>Null dacă plasarea e validă, altfel mesajul de eroare.</returns>
        public string? ValidatePlacement(Guid? unitId, OrgUnitType type, Guid? parentId)
        {
            if (parentId is null) return null;

            if (!_byId.TryGetValue(parentId.Value, out var parent))
                return "Subdiviziunea-părinte nu există.";

            if (unitId.HasValue && IsInSubtree(parentId.Value, unitId.Value))
                return "O subdiviziune nu poate fi mutată sub ea însăși sau sub una dintre subunitățile ei.";

            if (type <= parent.Type)
                return "Nivelul ales trebuie să fie inferior nivelului subdiviziunii-părinte " +
                       $"(„{parent.Name}”). Ordinea nivelurilor se vede și se modifică din „Niveluri”.";

            if (unitId.HasValue)
            {
                // Mutarea unei subdiviziuni cu copii: și copiii trebuie să rămână
                // cu un nivel mai mare decât noul lor părinte.
                var badChild = ChildrenOf(unitId.Value).FirstOrDefault(c => c.Type <= type);
                if (badChild is not null)
                    return $"Subunitatea „{badChild.Name}” ar ajunge pe un nivel nepermis.";
            }

            return null;
        }
    }
}
