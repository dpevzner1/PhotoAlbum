using PhotoAlbum.Core.Interfaces;
using System.Numerics;

namespace PhotoAlbum.App.Services;

/// <summary>
/// A cluster of MediaItem IDs that are all visually near-identical to each other.
/// Built with union-find so A~B and B~C produces one group {A,B,C}, not three pairs.
/// </summary>
public sealed record DuplicateGroup(IReadOnlyList<long> Ids, int MaxDistance);

/// <summary>
/// Finds visually near-identical photos by comparing 64-bit dHashes with
/// Hamming distance ≤ <see cref="Threshold"/>, then clusters via union-find.
/// </summary>
public sealed class DuplicateFinderService(IMediaItemRepository mediaRepo)
{
    public const int Threshold = 10;

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(CancellationToken ct = default)
    {
        var all = await mediaRepo.GetAllPHashesAsync(ct);
        if (all.Count == 0) return [];

        // Union-Find with path compression
        var parent = Enumerable.Range(0, all.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        // Compare all pairs; union matches and record max distance per root
        var maxDistByRoot = new Dictionary<int, int>();

        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                var dist = (int)BitOperations.PopCount(all[i].PHash ^ all[j].PHash);
                if (dist > Threshold) continue;

                Union(i, j);
                var root = Find(i);
                maxDistByRoot[root] = maxDistByRoot.TryGetValue(root, out var prev)
                    ? Math.Max(prev, dist) : dist;
            }
        }

        // Group MediaItem IDs by their union-find root
        var groups = new Dictionary<int, List<long>>();
        for (int i = 0; i < all.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = [];
            list.Add(all[i].Id);
        }

        return groups
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new DuplicateGroup(
                kv.Value,
                maxDistByRoot.GetValueOrDefault(kv.Key, 0)))
            .ToList();
    }
}
