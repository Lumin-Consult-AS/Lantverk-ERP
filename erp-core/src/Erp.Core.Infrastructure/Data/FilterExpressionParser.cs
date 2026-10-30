using System.Globalization;
using System.Text;
using Erp.Core.Domain.Data;

namespace Erp.Core.Infrastructure.Data;

/// <summary>
/// Parses OData-like filter expressions (e.g. <c>status eq 'posted' and amount gt 100</c>)
/// into a strongly typed <see cref="FilterNode"/> syntax tree.
/// </summary>
public static class FilterExpressionParser
{
    public static FilterNode? Parse(string? filterExpression)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return null;
        }

        var tokens = Tokenize(filterExpression);
        if (tokens.Count == 0)
        {
            return null;
        }

        var index = 0;
        var root = ParseOrExpression(tokens, ref index);
        if (index < tokens.Count)
        {
            throw new RecordValidationException($"Unexpected token '{tokens[index].Text}' in filter expression.");
        }

        return root;
    }

    private static FilterNode ParseOrExpression(List<Token> tokens, ref int index)
    {
        var left = ParseAndExpression(tokens, ref index);
        var clauses = new List<FilterNode> { left };

        while (index < tokens.Count && IsIdentifier(tokens[index], "or"))
        {
            index++;
            clauses.Add(ParseAndExpression(tokens, ref index));
        }

        return clauses.Count == 1 ? clauses[0] : new FilterGroup(LogicalOperator.Or, clauses);
    }

    private static FilterNode ParseAndExpression(List<Token> tokens, ref int index)
    {
        var left = ParsePrimary(tokens, ref index);
        var clauses = new List<FilterNode> { left };

        while (index < tokens.Count && IsIdentifier(tokens[index], "and"))
        {
            index++;
            clauses.Add(ParsePrimary(tokens, ref index));
        }

        return clauses.Count == 1 ? clauses[0] : new FilterGroup(LogicalOperator.And, clauses);
    }

    private static FilterNode ParsePrimary(List<Token> tokens, ref int index)
    {
        if (index >= tokens.Count)
        {
            throw new RecordValidationException("Unexpected end of filter expression.");
        }

        if (tokens[index].Kind == TokenKind.LParen)
        {
            index++;
            var inner = ParseOrExpression(tokens, ref index);
            if (index >= tokens.Count || tokens[index].Kind != TokenKind.RParen)
            {
                throw new RecordValidationException("Missing closing parenthesis ')' in filter expression.");
            }
            index++;
            return inner;
        }

        if (tokens[index].Kind != TokenKind.Identifier)
        {
            throw new RecordValidationException($"Expected field name in filter expression, got '{tokens[index].Text}'.");
        }

        var fieldName = tokens[index].Text;
        index++;

        if (index >= tokens.Count || tokens[index].Kind != TokenKind.Identifier)
        {
            throw new RecordValidationException($"Expected operator after field '{fieldName}'.");
        }

        var opToken = tokens[index].Text.ToLowerInvariant();
        index++;

        if (opToken is "isempty" or "is_empty")
        {
            return new FilterCondition(fieldName, FilterOperator.IsEmpty);
        }

        var op = opToken switch
        {
            "eq" => FilterOperator.Eq,
            "ne" => FilterOperator.Ne,
            "gt" => FilterOperator.Gt,
            "ge" => FilterOperator.Ge,
            "lt" => FilterOperator.Lt,
            "le" => FilterOperator.Le,
            "in" => FilterOperator.In,
            "contains" => FilterOperator.Contains,
            _ => throw new RecordValidationException($"Unsupported filter operator '{opToken}'.")
        };

        if (op == FilterOperator.In)
        {
            if (index >= tokens.Count || tokens[index].Kind != TokenKind.LParen)
            {
                throw new RecordValidationException("Expected '(' after 'in' operator.");
            }
            index++;

            var values = new List<object?>();
            while (index < tokens.Count && tokens[index].Kind != TokenKind.RParen)
            {
                values.Add(ParseLiteralValue(tokens[index]));
                index++;
                if (index < tokens.Count && tokens[index].Kind == TokenKind.Comma)
                {
                    index++;
                }
            }

            if (index >= tokens.Count || tokens[index].Kind != TokenKind.RParen)
            {
                throw new RecordValidationException("Missing ')' after 'in' list.");
            }
            index++;

            return new FilterCondition(fieldName, FilterOperator.In, values);
        }

        if (index >= tokens.Count)
        {
            throw new RecordValidationException($"Expected value after operator '{opToken}'.");
        }

        var literal = ParseLiteralValue(tokens[index]);
        index++;

        return new FilterCondition(fieldName, op, literal);
    }

    private static object? ParseLiteralValue(Token token)
    {
        if (token.Kind == TokenKind.StringLiteral)
        {
            return token.Text;
        }

        if (string.Equals(token.Text, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(token.Text, out var boolVal))
        {
            return boolVal;
        }

        if (long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
        {
            return longVal;
        }

        if (decimal.TryParse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var decVal))
        {
            return decVal;
        }

        return token.Text;
    }

    private static bool IsIdentifier(Token token, string expected)
        => token.Kind == TokenKind.Identifier && string.Equals(token.Text, expected, StringComparison.OrdinalIgnoreCase);

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < input.Length)
        {
            var ch = input[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (ch == '(')
            {
                tokens.Add(new Token(TokenKind.LParen, "("));
                i++;
                continue;
            }

            if (ch == ')')
            {
                tokens.Add(new Token(TokenKind.RParen, ")"));
                i++;
                continue;
            }

            if (ch == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                i++;
                continue;
            }

            if (ch is '\'' or '"')
            {
                var quote = ch;
                i++;
                var sb = new StringBuilder();
                while (i < input.Length && input[i] != quote)
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        i++;
                    }
                    sb.Append(input[i]);
                    i++;
                }

                if (i >= input.Length)
                {
                    throw new RecordValidationException("Unterminated string literal in filter expression.");
                }
                i++; // consume closing quote
                tokens.Add(new Token(TokenKind.StringLiteral, sb.ToString()));
                continue;
            }

            var start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '(' && input[i] != ')' && input[i] != ',')
            {
                i++;
            }

            tokens.Add(new Token(TokenKind.Identifier, input[start..i]));
        }

        return tokens;
    }

    private enum TokenKind
    {
        Identifier,
        StringLiteral,
        LParen,
        RParen,
        Comma
    }

    private sealed record Token(TokenKind Kind, string Text);
}

