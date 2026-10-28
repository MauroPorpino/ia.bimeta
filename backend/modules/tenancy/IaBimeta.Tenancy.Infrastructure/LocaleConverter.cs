using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.Tenancy.Infrastructure;

/// <summary>Guarda o idioma como texto BCP 47 na forma canônica (<c>pt-BR</c>).</summary>
internal sealed class LocaleConverter : ValueConverter<Locale, string>
{
    public LocaleConverter()
        : base(locale => locale.ToString(), texto => Locale.Parse(texto))
    {
    }
}
