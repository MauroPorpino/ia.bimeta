// O comando de operação "provision" (coloca uma empresa em funcionamento) roda e termina, sem subir o Api.
if (args.Length > 0 && string.Equals(args[0], IaBimeta.Api.Provisioning.ProvisionCommand.Verb, StringComparison.Ordinal))
{
    return await IaBimeta.Api.Provisioning.ProvisionCommand.RunAsync(args[1..], Console.Out);
}

var app = IaBimeta.Api.ApiHost.Build(args);

app.Run();
return 0;
