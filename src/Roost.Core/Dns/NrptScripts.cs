namespace Roost.Core.Dns;

/// <summary>
/// Lo que ejecuta <c>roost-helper</c> (elevado) para poner o quitar la regla NRPT de <c>.test</c>.
/// Como <c>HostsBlock</c>, se compila también dentro del helper (enlazado como fuente), así que no
/// puede depender de nada más de Roost. Todo es fijo: ni el espacio de nombres, ni la dirección, ni
/// el script vienen de fuera.
/// </summary>
internal static class NrptScripts
{
    public const string Namespace = ".test";
    public const string Address = "127.53.0.1";
    public const string Comment = "Roost";

    /// <summary>Código de salida de los scripts cuando ya hay una regla de <c>.test</c> que no es de Roost.</summary>
    public const int ForeignRuleExit = 3;

    private const string ForeignRuleExitText = "3";

    /// <summary>
    /// Pone la regla (quitando antes una vieja de Roost, para que repetirlo no la duplique) y vacía
    /// la caché del DNS. Si otra herramienta ya tiene una regla para <c>.test</c>, no la pisa.
    /// </summary>
    public const string Enable = $$"""
        $ErrorActionPreference = 'Stop'
        $rules = @(Get-DnsClientNrptRule | Where-Object { $_.Namespace -contains '{{Namespace}}' })
        if ($rules | Where-Object { $_.Comment -ne '{{Comment}}' }) { exit {{ForeignRuleExitText}} }
        $rules | Remove-DnsClientNrptRule -Force
        Add-DnsClientNrptRule -Namespace '{{Namespace}}' -NameServers '{{Address}}' -Comment '{{Comment}}'
        Clear-DnsClientCache
        exit 0
        """;

    /// <summary>Quita solo la regla de Roost y vacía la caché del DNS.</summary>
    public const string Disable = $$"""
        $ErrorActionPreference = 'Stop'
        Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '{{Comment}}' } | Remove-DnsClientNrptRule -Force
        Clear-DnsClientCache
        exit 0
        """;
}
