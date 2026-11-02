namespace Lims.Platform;

/// <summary>Roles from design 9.1.</summary>
public static class Roles
{
    public const string Technologist = "technologist";
    public const string Approver = "approver";
    public const string LabManager = "lab_manager";
    public const string IntegrationOperator = "integration_operator";
    public const string Administrator = "administrator";
    public const string Auditor = "auditor";
    public const string RawMessageViewer = "raw_message_viewer";

    public static readonly string[] All = [Technologist, Approver, LabManager, IntegrationOperator, Administrator, Auditor, RawMessageViewer];

    /// <summary>Roles that must use TOTP MFA (design 9.2).</summary>
    public static readonly string[] RequireMfa = [Approver, LabManager, Administrator, IntegrationOperator];
}

/// <summary>Raised when a workflow rule refuses an action; maps to HTTP 409/422 at the API edge.</summary>
public sealed class WorkflowException(string message) : Exception(message);
