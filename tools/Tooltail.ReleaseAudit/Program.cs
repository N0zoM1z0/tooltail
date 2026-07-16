using Tooltail.ReleaseAudit;

return await ReleaseAuditApplication.RunAsync(
    args,
    Console.Out,
    Console.Error).ConfigureAwait(false);
