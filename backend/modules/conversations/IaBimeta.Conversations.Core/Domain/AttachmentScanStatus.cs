namespace IaBimeta.Conversations.Domain;

/// <summary>Se o conteúdo de um arquivo guardado foi verificado contra código malicioso (ADR 0032). Um arquivo recusado por conter código malicioso nunca é guardado, então só existem estes dois estados.</summary>
public enum AttachmentScanStatus
{
    /// <summary>O arquivo foi guardado sem passar por um verificador (o verificador não estava ligado ou estava fora do ar e a varredura não era exigida).</summary>
    NotScanned = 0,

    /// <summary>O verificador declarou o conteúdo limpo.</summary>
    Clean = 1,
}
