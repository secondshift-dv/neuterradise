using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Ui;

internal static class MediaSurfaceText
{
    private static bool IsIndonesian =>
        string.Equals(SurfaceText.CurrentLanguage, "id", StringComparison.OrdinalIgnoreCase);

    private static string L(string english, string indonesian) => IsIndonesian ? indonesian : english;

    public static string TypeFilter(MediaTypeFilter value) => value switch
    {
        MediaTypeFilter.All => UI.T("MediaGrid.Type.All", "All types"),
        MediaTypeFilter.Images => UI.T("MediaGrid.Type.Images", "Images"),
        MediaTypeFilter.Videos => UI.T("MediaGrid.Type.Videos", "Videos"),
        MediaTypeFilter.Models => UI.T("MediaGrid.Type.Models", "Models"),
        _ => value.ToString(),
    };

    public static string RelationFilter(MediaRelationFilter value) => value switch
    {
        MediaRelationFilter.All => UI.T("MediaGrid.Relation.All", "All relations"),
        MediaRelationFilter.Owned => UI.T("MediaGrid.Relation.Owned", "Owned"),
        MediaRelationFilter.AppearsIn => UI.T("MediaGrid.Relation.AppearsIn", "Appears In"),
        MediaRelationFilter.Manual => UI.T("MediaGrid.Relation.Manual", "Manual"),
        _ => value.ToString(),
    };

    public static string Sort(MediaGridSort value) => value switch
    {
        MediaGridSort.NewestFirst => UI.T("MediaGrid.Sort.NewestFirst", "Newest first"),
        MediaGridSort.OldestFirst => UI.T("MediaGrid.Sort.OldestFirst", "Oldest first"),
        MediaGridSort.NameAscending => UI.T("MediaGrid.Sort.NameAscending", "Name A-Z"),
        MediaGridSort.CapturedNewestFirst => UI.T("MediaGrid.Sort.CapturedNewestFirst", "Captured newest"),
        MediaGridSort.SizeLargestFirst => UI.T("MediaGrid.Sort.SizeLargestFirst", "Largest first"),
        _ => value.ToString(),
    };

    public static string State(MediaGridState value) => value switch
    {
        MediaGridState.Loading => UI.T("MediaGrid.State.Loading", "Loading media…"),
        MediaGridState.EmptyProfileMedia => UI.T("MediaGrid.State.Empty", "This Profile does not have media yet."),
        MediaGridState.FilteredNoResults => UI.T("MediaGrid.State.NoResults", "No media matches these filters."),
        MediaGridState.BackgroundUpdating => UI.T("MediaGrid.State.Updating", "Media is updating in the background."),
        MediaGridState.RecoverablePreviewFailure => UI.T("MediaGrid.State.PreviewFailure", "A preview could not be shown. Open the media item for details."),
        MediaGridState.RecoverableQueryError => UI.T("MediaGrid.State.QueryError", "Media could not be loaded. Try refreshing the Profile."),
        _ => string.Empty,
    };

    public static string Range(MediaGridViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (viewModel.TotalCount == 0)
        {
            return UI.F("MediaGrid.Range", "{0}–{1} of {2}", 0, 0, 0);
        }

        var start = ((viewModel.CurrentPage - 1) * viewModel.PageSize) + 1;
        var end = Math.Min(viewModel.CurrentPage * viewModel.PageSize, viewModel.TotalCount);
        return UI.F("MediaGrid.Range", "{0}–{1} of {2}", start, end, viewModel.TotalCount);
    }

    public static string Type(MediaType value) => value switch
    {
        MediaType.Image => UI.T("Media.Type.Image", "Image"),
        MediaType.Video => UI.T("Media.Type.Video", "Video"),
        MediaType.Model => UI.T("Media.Type.Model", "Model"),
        _ => value.ToString(),
    };

    public static string AssetStatus(AssetState value) => value switch
    {
        AssetState.Candidate => L("Candidate", "Kandidat"),
        AssetState.Active => L("Active", "Aktif"),
        AssetState.Trashed => L("In Trash", "Di Sampah"),
        AssetState.Retired => L("Retired", "Dinonaktifkan"),
        _ => value.ToString(),
    };

    public static string ReconciliationState(ManagedPathState value) => value switch
    {
        ManagedPathState.None => L("None", "Tidak ada"),
        ManagedPathState.Pending => L("Pending", "Tertunda"),
        ManagedPathState.NeedsAttention => L("Needs attention", "Perlu perhatian"),
        _ => value.ToString(),
    };

    public static string InspectorFeedback(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Trim() switch
        {
            "File path copied to clipboard." => L("File path copied to clipboard.", "Jalur file disalin ke clipboard."),
            "File name copied to clipboard." => L("File name copied to clipboard.", "Nama file disalin ke clipboard."),
            "Set as Profile cover." => L("Set as Profile cover.", "Ditetapkan sebagai Cover Profil."),
            "Set as Profile banner." => L("Set as Profile banner.", "Ditetapkan sebagai Banner Profil."),
            "Primary Profile changed." => L("Primary Profile changed.", "Profil utama diubah."),
            "Association added." => L("Association added.", "Asosiasi ditambahkan."),
            "Association removed." => L("Association removed.", "Asosiasi dihapus."),
            "Moved to Trash (reversible)." => L("Moved to Trash (reversible).", "Dipindahkan ke Sampah (dapat dipulihkan)."),
            _ => L("The media action could not be completed.", "Tindakan media tidak dapat diselesaikan."),
        };
    }

    public static string AppearanceBasis(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }

        return raw.Trim() switch
        {
            "Confirmed Appearance" => L("Confirmed appearance", "Kemunculan terkonfirmasi"),
            "Confirmed Face" => L("Confirmed face", "Wajah terkonfirmasi"),
            "Suggested Appearance" => L("Suggested appearance", "Kemunculan yang disarankan"),
            "Manual" => L("Manual", "Manual"),
            _ => L("Appearance evidence", "Bukti kemunculan"),
        };
    }

    public static string DetailGroup(string raw) => raw switch
    {
        "File" => L("File", "File"),
        "Image" => L("Image", "Gambar"),
        "Color" => L("Color", "Warna"),
        "Camera" => L("Camera", "Kamera"),
        "Dates" => L("Dates", "Tanggal"),
        "Video" => L("Video", "Video"),
        "Audio" => L("Audio", "Audio"),
        "Metadata" => L("Metadata", "Metadata"),
        "3D Model" => L("3D Model", "Model 3D"),
        "Details" => L("Details", "Detail"),
        "Library Location" => L("Library Location", "Lokasi Pustaka"),
        "System Details" => L("System Details", "Detail Sistem"),
        "Raw Metadata" => L("Raw Metadata", "Metadata Mentah"),
        _ => raw,
    };

    public static string DetailField(string raw) => raw switch
    {
        "File Name" => L("File Name", "Nama File"),
        "Original Name" => L("Original Name", "Nama Asli"),
        "Format" => L("Format", "Format"),
        "File Size" => L("File Size", "Ukuran File"),
        "Dimensions" => L("Dimensions", "Dimensi"),
        "Aspect Ratio" => L("Aspect Ratio", "Rasio Aspek"),
        "Orientation" => L("Orientation", "Orientasi"),
        "Bit Depth" => L("Bit Depth", "Kedalaman Bit"),
        "Color Space" => L("Color Space", "Ruang Warna"),
        "Color Profile" => L("Color Profile", "Profil Warna"),
        "Camera Make" => L("Camera Make", "Merek Kamera"),
        "Camera Model" => L("Camera Model", "Model Kamera"),
        "Lens" => L("Lens", "Lensa"),
        "Focal Length" => L("Focal Length", "Panjang Fokus"),
        "Aperture" => L("Aperture", "Bukaan"),
        "Shutter Speed" => L("Shutter Speed", "Kecepatan Rana"),
        "ISO" => "ISO",
        "Exposure Program" => L("Exposure Program", "Program Eksposur"),
        "Exposure Bias" => L("Exposure Bias", "Kompensasi Eksposur"),
        "Flash" => L("Flash", "Lampu Kilat"),
        "Captured" => L("Captured", "Diambil"),
        "Created" => L("Created", "Dibuat"),
        "Added to Library" => L("Added to Library", "Ditambahkan ke Pustaka"),
        "Duration" => L("Duration", "Durasi"),
        "Video Codec" => L("Video Codec", "Codec Video"),
        "Profile" => L("Profile", "Profil"),
        "Frame Rate" => L("Frame Rate", "Laju Bingkai"),
        "Bitrate" => L("Bitrate", "Bitrate"),
        "Pixel Format" => L("Pixel Format", "Format Piksel"),
        "Audio Codec" => L("Audio Codec", "Codec Audio"),
        "Channels" => L("Channels", "Kanal"),
        "Sample Rate" => L("Sample Rate", "Laju Sampel"),
        "Audio Bitrate" => L("Audio Bitrate", "Bitrate Audio"),
        "Color Primaries" => L("Color Primaries", "Primer Warna"),
        "Color Transfer" => L("Color Transfer", "Transfer Warna"),
        "HDR" => "HDR",
        "Container" => L("Container", "Kontainer"),
        "Rotation" => L("Rotation", "Rotasi"),
        "Device Make" => L("Device Make", "Merek Perangkat"),
        "Device Model" => L("Device Model", "Model Perangkat"),
        "Software" => L("Software", "Perangkat Lunak"),
        "Encoder" => L("Encoder", "Encoder"),
        "Location" => L("Location", "Lokasi"),
        "Recorded" => L("Recorded", "Direkam"),
        "Meshes" => L("Meshes", "Mesh"),
        "Vertices" => L("Vertices", "Vertex"),
        "Triangles" => L("Triangles", "Segitiga"),
        "Materials" => L("Materials", "Material"),
        "Textures" => L("Textures", "Tekstur"),
        "Animations" => L("Animations", "Animasi"),
        "Cameras" => L("Cameras", "Kamera"),
        "Lights" => L("Lights", "Cahaya"),
        "Bounds" => L("Bounds", "Batas"),
        "Units" => L("Units", "Satuan"),
        "Adapter" => L("Adapter", "Adapter"),
        "Adapter Version" => L("Adapter Version", "Versi Adapter"),
        "Adapter Status" => L("Adapter Status", "Status Adapter"),
        "Dependency Status" => L("Dependency Status", "Status Dependensi"),
        "Bundle SHA256" => "Bundle SHA256",
        "Library Location (current)" => L("Library Location (current)", "Lokasi Pustaka (saat ini)"),
        "Pending Target Location" => L("Pending Target Location", "Lokasi Tujuan Tertunda"),
        "Reconciliation State" => L("Reconciliation State", "Status Rekonsiliasi"),
        "Status" => L("Status", "Status"),
        "File Fingerprint" => L("File Fingerprint", "Sidik Jari File"),
        "Asset ID" => "Asset ID",
        _ => raw,
    };

    public static string DetailValue(string field, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "—")
        {
            return raw;
        }

        if (field == "Status" && Enum.TryParse<AssetState>(raw, ignoreCase: true, out var assetState))
        {
            return AssetStatus(assetState);
        }

        if (field == "Reconciliation State"
            && Enum.TryParse<ManagedPathState>(raw, ignoreCase: true, out var pathState))
        {
            return ReconciliationState(pathState);
        }

        return raw switch
        {
            "Pending reconciliation" => L("Pending reconciliation", "Menunggu rekonsiliasi"),
            "Yes" => L("Yes", "Ya"),
            "No" => L("No", "Tidak"),
            "No (SDR)" => L("No (SDR)", "Tidak (SDR)"),
            "SelfContained" => L("Self-contained", "Mandiri"),
            "DependenciesMissing" => L("Dependencies missing", "Dependensi hilang"),
            "DependenciesUnknown" => L("Dependencies unknown", "Status dependensi tidak diketahui"),
            _ => raw,
        };
    }
}
