using Tooltail.ReleaseAudit;

return args.FirstOrDefault() is "pack-portable" or "verify-uninstall"
    ? await PortablePackageApplication.RunAsync(
        args,
        Console.Out,
        Console.Error).ConfigureAwait(false)
    : await ReleaseAuditApplication.RunAsync(
        args,
        Console.Out,
        Console.Error).ConfigureAwait(false);
