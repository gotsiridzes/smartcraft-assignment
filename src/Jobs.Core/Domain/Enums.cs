namespace Jobs.Core.Domain;

// Enums are stored as strings, so names are part of the schema. Rename only with a migration.

public enum JobStatus
{
    Draft,
    InProgress,
    Completed,
    Exported,
    ExportFailed,
}

public enum LineType
{
    Labour,
    Material,
}

public enum ExportTaskStatus
{
    Pending,
    Processing,
    Succeeded,
    DeadLettered,
}
