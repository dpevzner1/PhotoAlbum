using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public interface IIndexedFolderRepository
{
    /// <summary>Insert or update a folder record and return its Id.</summary>
    Task<long> UpsertAsync(string folderPath, string? label = null, CancellationToken ct = default);

    /// <summary>All indexed folders with live aggregate counts and sizes.</summary>
    Task<IReadOnlyList<IndexedFolder>> GetAllAsync(CancellationToken ct = default);

    Task<IndexedFolder?> GetByPathAsync(string folderPath, CancellationToken ct = default);
}
