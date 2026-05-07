using System.Diagnostics;
using System.IO;
using Dapper;
using Npgsql;
using RAMSOfficial.Helpers;

namespace RAMSOfficial.Services;

/// <summary>
/// Specialized service for handling employee profile photo retrieval from the profile_photos table.
/// Supports:
/// - Direct binary retrieval from profile_photos table
/// - MIME type detection
/// - Photo metadata (created_at, updated_at)
/// - Async loading with error handling
/// - Local file fallback
/// </summary>
public class ProfilePhotoService
{
    private readonly string _connectionString;

    public ProfilePhotoService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Represents profile photo data with metadata from the profile_photos table.
    /// </summary>
    public class ProfilePhotoData
    {
        public byte[]? ImageData { get; set; }
        public string? MimeType { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int PhotoId { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Get profile photo data for an employee from the profile_photos table.
    /// This retrieves the raw binary image data along with metadata.
    /// </summary>
    public async Task<ProfilePhotoData?> GetEmployeePhotoDataAsync(int employeeId)
    {
        try
        {
            if (employeeId <= 0)
                return null;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var result = await connection.QueryFirstOrDefaultAsync<(byte[] ImageData, string? MimeType, DateTime CreatedAt, DateTime UpdatedAt, int PhotoId)>(@"
                SELECT 
                    image_data, 
                    mime_type, 
                    created_at, 
                    updated_at,
                    photo_id
                FROM profile_photos
                WHERE entity_type = 'employee'
                    AND entity_id = @EmployeeId
                    AND image_data IS NOT NULL
                ORDER BY updated_at DESC
                LIMIT 1
            ", new { EmployeeId = employeeId });

            if (result == default)
                return null;

            return new ProfilePhotoData
            {
                ImageData = result.ImageData,
                MimeType = result.MimeType ?? "image/jpeg",
                CreatedAt = result.CreatedAt,
                UpdatedAt = result.UpdatedAt,
                PhotoId = result.PhotoId
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfilePhotoService] Error retrieving employee photo: {ex.Message}");
            return new ProfilePhotoData { Error = ex.Message };
        }
    }

    /// <summary>
    /// Get profile photo binary data for an employee (shortcut method).
    /// Returns raw image bytes suitable for BitmapImage loading.
    /// </summary>
    public async Task<byte[]?> GetEmployeePhotoBytesAsync(int employeeId)
    {
        var photoData = await GetEmployeePhotoDataAsync(employeeId);
        return photoData?.ImageData;
    }

    /// <summary>
    /// Check if an employee has a profile photo in the database.
    /// Useful for determining whether to show avatar fallback.
    /// </summary>
    public async Task<bool> HasEmployeePhotoAsync(int employeeId)
    {
        try
        {
            if (employeeId <= 0)
                return false;

            using var connection = new NpgsqlConnection(_connectionString);
            
            var exists = await connection.QueryFirstOrDefaultAsync<bool>(@"
                SELECT EXISTS(
                    SELECT 1 FROM profile_photos
                    WHERE entity_type = 'employee'
                        AND entity_id = @EmployeeId
                        AND image_data IS NOT NULL
                    LIMIT 1
                )
            ", new { EmployeeId = employeeId });

            return exists;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfilePhotoService] Error checking for employee photo: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get all profile photos for a list of employee IDs.
    /// Returns a dictionary mapping employee ID to photo data.
    /// Useful for batch loading photos in lists/grids.
    /// </summary>
    public async Task<Dictionary<int, ProfilePhotoData>> GetBatchEmployeePhotosAsync(IEnumerable<int> employeeIds)
    {
        var result = new Dictionary<int, ProfilePhotoData>();

        try
        {
            var ids = employeeIds.ToList();
            if (!ids.Any())
                return result;

            using var connection = new NpgsqlConnection(_connectionString);
            
            var photos = await connection.QueryAsync<(int EntityId, byte[] ImageData, string? MimeType, DateTime CreatedAt, DateTime UpdatedAt, int PhotoId)>(@"
                SELECT 
                    entity_id,
                    image_data, 
                    mime_type, 
                    created_at, 
                    updated_at,
                    photo_id
                FROM profile_photos
                WHERE entity_type = 'employee'
                    AND entity_id = ANY(@EmployeeIds)
                    AND image_data IS NOT NULL
                ORDER BY entity_id, updated_at DESC
            ", new { EmployeeIds = ids.ToArray() });

            // Group by entity_id and take the most recent photo for each employee
            var grouped = photos.GroupBy(p => p.EntityId);
            foreach (var group in grouped)
            {
                var latest = group.First();
                result[latest.EntityId] = new ProfilePhotoData
                {
                    ImageData = latest.ImageData,
                    MimeType = latest.MimeType ?? "image/jpeg",
                    CreatedAt = latest.CreatedAt,
                    UpdatedAt = latest.UpdatedAt,
                    PhotoId = latest.PhotoId
                };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfilePhotoService] Error batch retrieving employee photos: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Save a profile photo to the database.
    /// If a photo already exists for the employee, updates it.
    /// </summary>
    public async Task<bool> SaveEmployeePhotoAsync(int employeeId, byte[] imageData, string? mimeType = null)
    {
        try
        {
            if (employeeId <= 0 || imageData == null || imageData.Length == 0)
                return false;

            mimeType ??= DetectMimeType(imageData);
            var now = DateTime.UtcNow;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Try to update existing photo first
            var result = await connection.ExecuteAsync(@"
                UPDATE profile_photos
                SET image_data = @ImageData,
                    mime_type = @MimeType,
                    updated_at = @UpdatedAt
                WHERE entity_type = 'employee' 
                    AND entity_id = @EmployeeId
            ", new 
            { 
                ImageData = imageData, 
                MimeType = mimeType, 
                UpdatedAt = now,
                EmployeeId = employeeId 
            });

            // If no existing photo, insert new one
            if (result == 0)
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO profile_photos 
                    (entity_type, entity_id, mime_type, image_data, created_at, updated_at)
                    VALUES ('employee', @EmployeeId, @MimeType, @ImageData, @CreatedAt, @UpdatedAt)
                ", new 
                { 
                    EmployeeId = employeeId,
                    MimeType = mimeType, 
                    ImageData = imageData, 
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfilePhotoService] Error saving employee photo: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete profile photo for an employee.
    /// </summary>
    public async Task<bool> DeleteEmployeePhotoAsync(int employeeId)
    {
        try
        {
            if (employeeId <= 0)
                return false;

            using var connection = new NpgsqlConnection(_connectionString);
            
            var result = await connection.ExecuteAsync(@"
                DELETE FROM profile_photos
                WHERE entity_type = 'employee' 
                    AND entity_id = @EmployeeId
            ", new { EmployeeId = employeeId });

            return result > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfilePhotoService] Error deleting employee photo: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Detect MIME type from image binary data by checking magic numbers (file signatures).
    /// Supports: JPEG, PNG, GIF, BMP, WebP, TIFF
    /// </summary>
    private static string DetectMimeType(byte[] imageData)
    {
        if (imageData == null || imageData.Length < 4)
            return "image/jpeg"; // Default to JPEG

        // Check magic numbers
        if (imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF)
            return "image/jpeg";

        if (imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
            return "image/png";

        if (imageData[0] == 0x47 && imageData[1] == 0x49 && imageData[2] == 0x46)
            return "image/gif";

        if (imageData[0] == 0x42 && imageData[1] == 0x4D)
            return "image/bmp";

        if (imageData[0] == 0x52 && imageData[1] == 0x49 && imageData[2] == 0x46 && imageData[3] == 0x46)
            return "image/webp";

        if ((imageData[0] == 0x49 && imageData[1] == 0x49 && imageData[2] == 0x2A && imageData[3] == 0x00) ||
            (imageData[0] == 0x4D && imageData[1] == 0x4D && imageData[2] == 0x00 && imageData[3] == 0x2A))
            return "image/tiff";

        return "image/jpeg";
    }

    /// <summary>
    /// Get photo statistics for diagnostics.
    /// Returns count of photos in the database.
    /// </summary>
    public async Task<(int TotalPhotos, int EmployeePhotos, long TotalBytes)> GetPhotoStatisticsAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            var stats = await connection.QueryFirstOrDefaultAsync<(int TotalPhotos, int EmployeePhotos, long TotalBytes)>(@"
                SELECT 
                    COUNT(*) as TotalPhotos,
                    COALESCE(SUM(CASE WHEN entity_type = 'employee' THEN 1 ELSE 0 END), 0) as EmployeePhotos,
                    COALESCE(SUM(OCTET_LENGTH(image_data)), 0) as TotalBytes
                FROM profile_photos
                WHERE image_data IS NOT NULL
            ");

            return stats;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfilePhotoService] Error retrieving photo statistics: {ex.Message}");
            return (0, 0, 0);
        }
    }
}
