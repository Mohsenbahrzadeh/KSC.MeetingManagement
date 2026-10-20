namespace KSC.MeetingManagement.Domain.Meetings;

/// <summary>Metadata of a file attached to a meeting. The bytes live in file storage under <see cref="StorageKey"/>.</summary>
public sealed class MeetingAttachment
{
    public MeetingAttachment(
        Guid id, Guid tenantId, Guid meetingId, string fileName, string contentType, long sizeBytes,
        string sha256, string storageKey, Guid uploadedBy, DateTimeOffset uploadedAtUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty || uploadedBy == Guid.Empty)
        {
            throw new MeetingDomainException("Attachment, tenant, meeting and uploader ids are required.");
        }

        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(storageKey))
        {
            throw new MeetingDomainException("File name and storage key are required.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        UploadedBy = uploadedBy;
        UploadedAtUtc = uploadedAtUtc;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public string FileName { get; }
    public string ContentType { get; }
    public long SizeBytes { get; }
    public string Sha256 { get; }
    public string StorageKey { get; }
    public Guid UploadedBy { get; }
    public DateTimeOffset UploadedAtUtc { get; }
}
