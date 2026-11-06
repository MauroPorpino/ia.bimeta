namespace IaBimeta.Api.Operations;

/// <summary>
/// O log dos comandos de operação (<c>status</c>, <c>retention</c>, <c>provision</c>, <c>reset-password</c>): só avisos e erros no console, porque a saída do comando é o relatório. O <c>appsettings.Development.json</c> define
/// Information para tudo (útil ao subir o Api, ruído num comando de operador, com o SQL de cada consulta do EF): o filtro sem categoria, registrado depois da configuração, vale mais que a regra padrão dela.
/// </summary>
internal static class OperationsLogging
{
    public static void Quiet(ILoggingBuilder logging)
    {
        logging.SetMinimumLevel(LogLevel.Warning);
        logging.AddFilter(null, LogLevel.Warning);

        // O aviso de modelo do EF sobre valores padrão do banco é conhecido e não é do operador.
        logging.AddFilter("Microsoft.EntityFrameworkCore.Model.Validation", LogLevel.Error);
    }
}
