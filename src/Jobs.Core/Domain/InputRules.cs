namespace Jobs.Core.Domain;

/// <summary>Collects field errors so a request reports every problem at once.</summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
    }

    public void ThrowIfAny()
    {
        if (_errors.Count > 0)
        {
            throw new DomainValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}

/// <summary>
/// Input rules shared by jobs and lines. Inputs arrive as raw client values (possibly null),
/// so presence checks live here too and every rule sits in the domain.
/// </summary>
internal static class InputRules
{
    public const int QuantityDecimals = 3;
    public const int PriceDecimals = 2;
    public const decimal MaxQuantity = 1_000_000m;
    public const decimal MaxUnitPrice = 100_000_000m;

    public static string Text(ValidationErrors errors, string field, string? value, int maxLength, bool required)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        if (required && trimmed.Length == 0)
        {
            errors.Add(field, "Required.");
        }
        else if (trimmed.Length > maxLength)
        {
            errors.Add(field, $"Must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static LineType Type(ValidationErrors errors, LineType? value)
    {
        if (value is null)
        {
            errors.Add("type", "Required.");
            return default;
        }

        if (!Enum.IsDefined(value.Value))
        {
            errors.Add("type", "Must be Labour or Material.");
        }

        return value.Value;
    }

    public static decimal Quantity(ValidationErrors errors, decimal? value)
    {
        if (value is null)
        {
            errors.Add("quantity", "Required.");
            return 0m;
        }

        if (value.Value <= 0m)
        {
            errors.Add("quantity", "Must be greater than 0.");
        }
        else if (value.Value > MaxQuantity)
        {
            errors.Add("quantity", $"Must be at most {MaxQuantity}.");
        }
        else if (decimal.Round(value.Value, QuantityDecimals) != value.Value)
        {
            errors.Add("quantity", $"Must have at most {QuantityDecimals} decimal places.");
        }

        return value.Value;
    }

    public static decimal UnitPrice(ValidationErrors errors, decimal? value)
    {
        if (value is null)
        {
            errors.Add("unitPrice", "Required.");
            return 0m;
        }

        if (value.Value < 0m)
        {
            errors.Add("unitPrice", "Must be 0 or greater.");
        }
        else if (value.Value > MaxUnitPrice)
        {
            errors.Add("unitPrice", $"Must be at most {MaxUnitPrice}.");
        }
        else if (decimal.Round(value.Value, PriceDecimals) != value.Value)
        {
            errors.Add("unitPrice", $"Must have at most {PriceDecimals} decimal places.");
        }

        return value.Value;
    }
}
