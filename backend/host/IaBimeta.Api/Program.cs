// Os comandos de operação ("migrate", que aplica as migrations; "provision", que coloca uma empresa em funcionamento; "reset-password", que emite o token de redefinição de senha; "retention"; "attachments"; "status") rodam e terminam, sem subir o Api.
if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Provisioning.ProvisionCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Provisioning.ProvisionCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Provisioning.ResetPasswordCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Provisioning.ResetPasswordCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Provisioning.MigrateCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Provisioning.MigrateCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Retention.RetentionCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Retention.RetentionCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Attachments.AttachmentsCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Attachments.AttachmentsCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Operations.StatusCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Operations.StatusCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Operations.WorkflowsCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Operations.WorkflowsCommand.RunAsync(args[1..], Console.Out);
}

var app = IaBimeta.Api.ApiHost.Build(args);

app.Run();
return 0;
