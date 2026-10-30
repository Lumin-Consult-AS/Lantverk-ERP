using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Erp.Client.Services;

/// <summary>
/// Provides client-side localization (<c>nb</c> and <c>en</c>) combining built-in platform
/// strings with application-supplied <c>i18n/</c> dictionaries fetched over REST.
/// </summary>
public sealed class I18nService
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{(?<key>[a-zA-Z0-9_\.]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, Dictionary<string, string>> BuiltInTranslations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nb"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ui.app.title"] = "Laftverk ERP",
            ["ui.nav.home"] = "Oversikt og arbeidsliste",
            ["ui.nav.tasks"] = "Oppgaver",
            ["ui.nav.workflows"] = "Godkjenningsflyter",
            ["ui.attention.title"] = "Krever handling",
            ["ui.attention.open"] = "Åpne",
            ["ui.attention.hide"] = "Skjul",
            ["ui.attention.snooze"] = "Utsett 1 dag",
            ["ui.attention.showHidden"] = "Vis skjulte",
            ["ui.tasks.title"] = "Oppgaver til godkjenning",
            ["ui.tasks.empty"] = "Du har ingen åpne oppgaver.",
            ["ui.tasks.approve"] = "Godkjenn",
            ["ui.tasks.reject"] = "Avvis",
            ["ui.tasks.delegate"] = "Delegér",
            ["ui.tasks.comment"] = "Kommentar",
            ["ui.tasks.due"] = "Frist",
            ["ui.tasks.escalated"] = "Eskalert",
            ["ui.tasks.delegateTo"] = "Delegér til",
            ["ui.workflows.title"] = "Godkjenningsflyter",
            ["ui.workflows.customized"] = "Tilpasset av tenanten",
            ["ui.workflows.default"] = "Standard fra appen",
            ["ui.workflows.mode"] = "Rekkefølge",
            ["ui.workflows.sequential"] = "Sekvensiell",
            ["ui.workflows.parallel"] = "Parallell",
            ["ui.workflows.delegation"] = "Delegering tillatt",
            ["ui.workflows.deadlineDays"] = "Frist (dager)",
            ["ui.workflows.escalateRole"] = "Eskaler til rolle",
            ["ui.workflows.steps"] = "Steg",
            ["ui.workflows.stepName"] = "Navn",
            ["ui.workflows.assigneeRole"] = "Rolle",
            ["ui.workflows.assigneeUser"] = "Bruker",
            ["ui.workflows.whenField"] = "Når felt",
            ["ui.workflows.whenOperator"] = "Operator",
            ["ui.workflows.whenValue"] = "Verdi",
            ["ui.workflows.compoundRule"] = "Sammensatt regel (beholdes uendret)",
            ["ui.workflows.addStep"] = "Legg til steg",
            ["ui.workflows.removeStep"] = "Fjern steg",
            ["ui.workflows.saved"] = "Flyten er lagret.",
            ["ui.nav.apps"] = "Installerte apper",
            ["ui.nav.pages"] = "Moduler og sider",
            ["ui.action.new"] = "Ny post",
            ["ui.action.save"] = "Lagre",
            ["ui.action.cancel"] = "Avbryt",
            ["ui.action.delete"] = "Slett",
            ["ui.action.deleteSelected"] = "Slett valgte",
            ["ui.action.refresh"] = "Oppdater",
            ["ui.action.search"] = "Søk...",
            ["ui.action.filter"] = "Filter (f.eks. status eq 'active')",
            ["ui.action.applyFilter"] = "Filtrer",
            ["ui.action.clearFilter"] = "Nullstill",
            ["ui.action.backToList"] = "Tilbake til liste",
            ["ui.action.addSubItem"] = "Legg til",
            ["ui.action.edit"] = "Rediger",
            ["ui.action.installCoreParties"] = "Installer grunnappen Parter (core.parties)",
            ["ui.action.uninstall"] = "Avinstaller",
            ["ui.status.emptyList"] = "Ingen rader funnet.",
            ["ui.status.saved"] = "Endringer er lagret.",
            ["ui.status.deleted"] = "Posten ble slettet.",
            ["ui.status.worklistEmpty"] = "Ingen åpne oppgaver eller unntak krever handling akkurat nå.",
            ["ui.validation.required"] = "Feltet '{0}' er påkrevd.",
            ["ui.validation.maxLength"] = "Feltet '{0}' kan maks være {1} tegn.",
            ["ui.validation.min"] = "Feltet '{0}' må være minst {1}.",
            ["ui.validation.max"] = "Feltet '{0}' kan maks være {1}.",
            ["ui.validation.pattern"] = "Feltet '{0}' har ugyldig format (krav: {1}).",
            ["ui.validation.readOnly"] = "Feltet '{0}' er skrivebeskyttet."
        },
        ["en"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ui.app.title"] = "Laftverk ERP",
            ["ui.nav.home"] = "Dashboard & Worklist",
            ["ui.nav.tasks"] = "Tasks",
            ["ui.nav.workflows"] = "Approval flows",
            ["ui.attention.title"] = "Needs attention",
            ["ui.attention.open"] = "Open",
            ["ui.attention.hide"] = "Hide",
            ["ui.attention.snooze"] = "Snooze 1 day",
            ["ui.attention.showHidden"] = "Show hidden",
            ["ui.tasks.title"] = "Tasks awaiting approval",
            ["ui.tasks.empty"] = "You have no open tasks.",
            ["ui.tasks.approve"] = "Approve",
            ["ui.tasks.reject"] = "Reject",
            ["ui.tasks.delegate"] = "Delegate",
            ["ui.tasks.comment"] = "Comment",
            ["ui.tasks.due"] = "Due",
            ["ui.tasks.escalated"] = "Escalated",
            ["ui.tasks.delegateTo"] = "Delegate to",
            ["ui.workflows.title"] = "Approval flows",
            ["ui.workflows.customized"] = "Customized by the tenant",
            ["ui.workflows.default"] = "App default",
            ["ui.workflows.mode"] = "Order",
            ["ui.workflows.sequential"] = "Sequential",
            ["ui.workflows.parallel"] = "Parallel",
            ["ui.workflows.delegation"] = "Delegation allowed",
            ["ui.workflows.deadlineDays"] = "Deadline (days)",
            ["ui.workflows.escalateRole"] = "Escalate to role",
            ["ui.workflows.steps"] = "Steps",
            ["ui.workflows.stepName"] = "Name",
            ["ui.workflows.assigneeRole"] = "Role",
            ["ui.workflows.assigneeUser"] = "User",
            ["ui.workflows.whenField"] = "When field",
            ["ui.workflows.whenOperator"] = "Operator",
            ["ui.workflows.whenValue"] = "Value",
            ["ui.workflows.compoundRule"] = "Compound rule (kept unchanged)",
            ["ui.workflows.addStep"] = "Add step",
            ["ui.workflows.removeStep"] = "Remove step",
            ["ui.workflows.saved"] = "The flow was saved.",
            ["ui.nav.apps"] = "Installed Apps",
            ["ui.nav.pages"] = "Modules & Pages",
            ["ui.action.new"] = "New record",
            ["ui.action.save"] = "Save",
            ["ui.action.cancel"] = "Cancel",
            ["ui.action.delete"] = "Delete",
            ["ui.action.deleteSelected"] = "Delete selected",
            ["ui.action.refresh"] = "Refresh",
            ["ui.action.search"] = "Search...",
            ["ui.action.filter"] = "Filter (e.g. status eq 'active')",
            ["ui.action.applyFilter"] = "Apply filter",
            ["ui.action.clearFilter"] = "Reset",
            ["ui.action.backToList"] = "Back to list",
            ["ui.action.addSubItem"] = "Add",
            ["ui.action.edit"] = "Edit",
            ["ui.action.installCoreParties"] = "Install base app Parties (core.parties)",
            ["ui.action.uninstall"] = "Uninstall",
            ["ui.status.emptyList"] = "No records found.",
            ["ui.status.saved"] = "Changes saved.",
            ["ui.status.deleted"] = "Record deleted.",
            ["ui.status.worklistEmpty"] = "No open tasks or exceptions require attention right now.",
            ["ui.validation.required"] = "Field '{0}' is required.",
            ["ui.validation.maxLength"] = "Field '{0}' cannot exceed {1} characters.",
            ["ui.validation.min"] = "Field '{0}' must be at least {1}.",
            ["ui.validation.max"] = "Field '{0}' must be at most {1}.",
            ["ui.validation.pattern"] = "Field '{0}' does not match required format ({1}).",
            ["ui.validation.readOnly"] = "Field '{0}' is read-only."
        }
    };

    private Dictionary<string, string> _appTranslations = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentLocale { get; private set; } = "nb";

    public event Action? OnLocaleChanged;

    public void SetLocale(string locale, IReadOnlyDictionary<string, string>? appTranslations = null)
    {
        CurrentLocale = string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "nb";
        if (appTranslations is not null)
        {
            _appTranslations = new Dictionary<string, string>(appTranslations, StringComparer.OrdinalIgnoreCase);
        }
        OnLocaleChanged?.Invoke();
    }

    public void LoadAppTranslations(IReadOnlyDictionary<string, string> appTranslations)
    {
        _appTranslations = new Dictionary<string, string>(appTranslations, StringComparer.OrdinalIgnoreCase);
        OnLocaleChanged?.Invoke();
    }

    public string T(string? key, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback ?? string.Empty;
        }

        if (_appTranslations.TryGetValue(key, out var appVal))
        {
            return appVal;
        }

        if (BuiltInTranslations.TryGetValue(CurrentLocale, out var builtIn)
            && builtIn.TryGetValue(key, out var builtInVal))
        {
            return builtInVal;
        }

        if (BuiltInTranslations.TryGetValue("nb", out var nbFallback)
            && nbFallback.TryGetValue(key, out var nbVal))
        {
            return nbVal;
        }

        return fallback ?? key;
    }

    public string Format(string key, params object[] args)
        => string.Format(CultureInfo.InvariantCulture, T(key), args);

    public string FormatRecordTitle(string? titleTemplate, IReadOnlyDictionary<string, object?> fields, string fallback)
    {
        if (string.IsNullOrWhiteSpace(titleTemplate))
        {
            return fallback;
        }

        if (!titleTemplate.Contains('{'))
        {
            return T(titleTemplate, fallback);
        }

        var formatted = PlaceholderRegex.Replace(titleTemplate, match =>
        {
            var fieldKey = match.Groups["key"].Value;
            if (fields.TryGetValue(fieldKey, out var val) && val is not null)
            {
                return UnwrapDisplayString(val);
            }

            var shortKey = fieldKey.Contains('.') ? fieldKey[(fieldKey.LastIndexOf('.') + 1)..] : fieldKey;
            if (fields.TryGetValue(shortKey, out var val2) && val2 is not null)
            {
                return UnwrapDisplayString(val2);
            }

            return string.Empty;
        }).Trim(' ', '–', '-');

        return string.IsNullOrWhiteSpace(formatted) ? fallback : formatted;
    }

    public static string UnwrapDisplayString(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.String => je.GetString() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object when je.TryGetProperty("amount", out var amt) && je.TryGetProperty("currency", out var cur)
                    => $"{amt} {cur.GetString()}",
                _ => je.ToString()
            };
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

