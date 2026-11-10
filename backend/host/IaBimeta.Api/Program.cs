// Os comandos de operação ("provision", que coloca uma empresa em funcionamento, e "reset-password", que emite o token de redefinição de senha) rodam e terminam, sem subir o Api.
if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Provisioning.ProvisionCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Provisioning.ProvisionCommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Provisioning.ResetPasswordCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Provisioning.ResetPasswordCommand.RunAsync(args[1..], Console.Out);
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

var app = IaBimeta.Api.ApiHost.Build(args);

app.Run();
return 0;
