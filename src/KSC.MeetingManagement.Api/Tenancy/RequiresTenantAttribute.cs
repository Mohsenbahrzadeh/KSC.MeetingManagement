namespace KSC.MeetingManagement.Api.Tenancy;

/// <summary>Marks endpoints that need a valid, active tenant. Apply to business controllers.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresTenantAttribute : Attribute;
