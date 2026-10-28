using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>Guarda o idioma como texto BCP 47 na forma canônica (<c>pt-BR</c>).</summary>
internal sealed class LocaleConverter : ValueConverter<Locale, string>
{
    /// <summary>
    /// Formato aceito pelo banco nas colunas de idioma: a forma geral de uma tag BCP 47. O aplicativo valida e normaliza
    /// (<see cref="Locale"/>); o banco recusa lixo (<c>pt_BR</c>, texto livre) vindo de qualquer outro caminho.
    /// </summary>
    public const string SqlPattern = "'^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'";

    public LocaleConverter()
        : base(locale => locale.ToString(), texto => Locale.Parse(texto))
    {
    }
}
