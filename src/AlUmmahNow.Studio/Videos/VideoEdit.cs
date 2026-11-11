using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Videos;

/// <summary>The edit form's values; <see cref="ChangesFrom"/> sends only what the user changed.</summary>
public sealed class VideoEdit
{
    public VideoEdit(StudioVideo v)
    {
        Original = v;
        Title = v.Title;
        Description = v.Description;
        Category = v.Category;
        Language = v.Language;
        IsPublic = v.IsPublic;
        Tags = string.Join(", ", v.Tags);
    }

    public StudioVideo Original { get; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string? Category { get; set; }
    public string? Language { get; set; }
    public bool IsPublic { get; set; }
    public string Tags { get; set; }

    /// <summary>An i18n key when the form can't be saved, else null.</summary>
    public string? Problem => string.IsNullOrWhiteSpace(Title) ? "studio.videos.titleRequired" : null;

    public VideoChanges ChangesFrom()
    {
        var o = Original;
        var tags = UploadPlanner.ParseTags(Tags);
        return new VideoChanges
        {
            Title = Title.Trim() != o.Title ? TitlePatterns.Truncate(Title.Trim(), TitlePatterns.MaxTitleLength) : null,
            Description = Description != o.Description ? Description : null,
            Category = Category != o.Category ? Category ?? "" : null,
            Language = Language != o.Language && Language != null ? Language : null,
            IsPublic = IsPublic != o.IsPublic ? IsPublic : null,
            Tags = tags.SequenceEqual(o.Tags) ? null : tags,
        };
    }
}
