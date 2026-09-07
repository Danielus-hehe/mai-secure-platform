using System;
using System.Collections.Generic;

namespace MAI.BusinessLogic.Dtos
{
    /// <summary>Parametrii de paginare acceptați de toate listele.</summary>
    public class PaginationQuery
    {
        private const int MaxPageSize = 100;
        private int _pageSize = 25;
        private int _page = 1;

        /// <summary>Pagina cerută, începe de la 1.</summary>
        public int Page
        {
            get => _page;
            set => _page = value < 1 ? 1 : value;
        }

        /// <summary>
        /// Câte înregistrări pe pagină. Plafonat la 100 — fără plafon, un client
        /// poate cere pageSize=1000000 și transforma endpointul într-un vector de DoS.
        /// </summary>
        public int PageSize
        {
            get => _pageSize;
            set => _pageSize = value switch
            {
                < 1           => 25,
                > MaxPageSize => MaxPageSize,
                _             => value,
            };
        }

        public int Skip => (Page - 1) * PageSize;
    }

    /// <summary>Rezultat paginat generic: elementele paginii curente plus metadate.</summary>
    public class PagedResult<T>
    {
        public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

        /// <summary>Numărul total de înregistrări care trec de filtre (nu doar pagina curentă).</summary>
        public int TotalCount { get; init; }

        public int Page { get; init; }
        public int PageSize { get; init; }

        public int TotalPages => PageSize <= 0
            ? 0
            : (int)Math.Ceiling(TotalCount / (double)PageSize);

        public bool HasPrevious => Page > 1;
        public bool HasNext => Page < TotalPages;

        public static PagedResult<T> Create(IReadOnlyList<T> items, int totalCount, PaginationQuery q) =>
            new()
            {
                Items      = items,
                TotalCount = totalCount,
                Page       = q.Page,
                PageSize   = q.PageSize,
            };
    }
}