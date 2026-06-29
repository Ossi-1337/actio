using System.Globalization;

namespace Actio.Core.Expressions;

public sealed class ExpressionParser
{
    public static ExpressionParseResult ParseTemplateExpression(string expression)
    {
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("${{", StringComparison.Ordinal) ||
            !trimmed.EndsWith("}}", StringComparison.Ordinal))
        {
            return ExpressionParseResult.Failed(["Expression must be wrapped in '${{ }}'."]);
        }

        var body = trimmed[3..^2].Trim();
        if (body.Length == 0)
        {
            return ExpressionParseResult.Failed(["Expression body is required."]);
        }

        return ParseExpression(body);
    }

    public static ExpressionParseResult ParseExpression(string expression)
    {
        var parser = new Parser(expression);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private readonly List<string> _errors = [];
        private Token _current;

        public Parser(string expression)
        {
            _lexer = new Lexer(expression);
            _current = _lexer.NextToken();
        }

        public ExpressionParseResult Parse()
        {
            var expression = ParseOr();
            AddLexerErrors();

            if (_current.Kind != TokenKind.End)
            {
                AddError($"Unexpected token '{_current.Text}' at position {_current.Position}.");
            }

            return _errors.Count == 0 && expression is not null
                ? ExpressionParseResult.Resolved(expression)
                : ExpressionParseResult.Failed(_errors);
        }

        private ExpressionNode? ParseOr()
        {
            var left = ParseAnd();
            while (Match(TokenKind.OrOr))
            {
                var right = ParseAnd();
                left = CreateBinary(left, ExpressionBinaryOperator.Or, right);
            }

            return left;
        }

        private ExpressionNode? ParseAnd()
        {
            var left = ParseEquality();
            while (Match(TokenKind.AndAnd))
            {
                var right = ParseEquality();
                left = CreateBinary(left, ExpressionBinaryOperator.And, right);
            }

            return left;
        }

        private ExpressionNode? ParseEquality()
        {
            var left = ParseComparison();
            while (_current.Kind is TokenKind.EqualsEquals or TokenKind.BangEquals)
            {
                var operatorKind = _current.Kind == TokenKind.EqualsEquals
                    ? ExpressionBinaryOperator.Equal
                    : ExpressionBinaryOperator.NotEqual;
                Advance();
                var right = ParseComparison();
                left = CreateBinary(left, operatorKind, right);
            }

            return left;
        }

        private ExpressionNode? ParseComparison()
        {
            var left = ParseUnary();
            while (_current.Kind is TokenKind.LessThan or TokenKind.LessThanOrEqual or TokenKind.GreaterThan or TokenKind.GreaterThanOrEqual)
            {
                var operatorKind = _current.Kind switch
                {
                    TokenKind.LessThan => ExpressionBinaryOperator.LessThan,
                    TokenKind.LessThanOrEqual => ExpressionBinaryOperator.LessThanOrEqual,
                    TokenKind.GreaterThan => ExpressionBinaryOperator.GreaterThan,
                    _ => ExpressionBinaryOperator.GreaterThanOrEqual
                };

                Advance();
                var right = ParseUnary();
                left = CreateBinary(left, operatorKind, right);
            }

            return left;
        }

        private ExpressionNode? ParseUnary()
        {
            if (Match(TokenKind.Bang))
            {
                var operand = ParseUnary();
                return operand is null ? null : new UnaryExpressionNode(ExpressionUnaryOperator.Not, operand);
            }

            return ParsePrimary();
        }

        private ExpressionNode? ParsePrimary()
        {
            var token = _current;
            switch (token.Kind)
            {
                case TokenKind.String:
                    Advance();
                    return new LiteralExpressionNode(ExpressionValue.FromString(token.Text));
                case TokenKind.Number:
                    Advance();
                    return decimal.TryParse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                        ? new LiteralExpressionNode(ExpressionValue.FromNumber(number))
                        : AddErrorExpression($"Invalid number literal '{token.Text}'.");
                case TokenKind.True:
                    Advance();
                    return new LiteralExpressionNode(ExpressionValue.FromBoolean(true));
                case TokenKind.False:
                    Advance();
                    return new LiteralExpressionNode(ExpressionValue.FromBoolean(false));
                case TokenKind.Null:
                    Advance();
                    return new LiteralExpressionNode(ExpressionValue.Null);
                case TokenKind.Identifier:
                    return ParseIdentifierExpression();
                case TokenKind.LeftParen:
                    Advance();
                    var expression = ParseOr();
                    if (!Match(TokenKind.RightParen))
                    {
                        AddError($"Expected ')' at position {_current.Position}.");
                    }

                    return expression;
                default:
                    return AddErrorExpression($"Unexpected token '{token.Text}' at position {token.Position}.");
            }
        }

        private ExpressionNode? ParseIdentifierExpression()
        {
            var name = _current.Text;
            Advance();

            if (Match(TokenKind.LeftParen))
            {
                return ParseFunctionCall(name);
            }

            var path = new List<string>();
            while (Match(TokenKind.Dot))
            {
                if (_current.Kind != TokenKind.Identifier)
                {
                    AddError($"Expected property name after '.' at position {_current.Position}.");
                    return null;
                }

                path.Add(_current.Text);
                Advance();
            }

            return new ReferenceExpressionNode(new ExpressionReference(name, path));
        }

        private FunctionCallExpressionNode? ParseFunctionCall(string name)
        {
            var arguments = new List<ExpressionNode>();
            var closed = false;

            if (Match(TokenKind.RightParen))
            {
                closed = true;
            }
            else
            {
                while (_current.Kind != TokenKind.End)
                {
                    var argument = ParseOr();
                    if (argument is not null)
                    {
                        arguments.Add(argument);
                    }

                    if (Match(TokenKind.RightParen))
                    {
                        closed = true;
                        break;
                    }

                    if (!Match(TokenKind.Comma))
                    {
                        AddError($"Expected ',' or ')' at position {_current.Position}.");
                        break;
                    }
                }

                if (!closed)
                {
                    AddError($"Expected ')' at position {_current.Position}.");
                }
            }

            return new FunctionCallExpressionNode(name, arguments);
        }

        private BinaryExpressionNode? CreateBinary(
            ExpressionNode? left,
            ExpressionBinaryOperator operatorKind,
            ExpressionNode? right)
        {
            return left is null || right is null ? null : new BinaryExpressionNode(left, operatorKind, right);
        }

        private bool Match(TokenKind kind)
        {
            if (_current.Kind != kind)
            {
                return false;
            }

            Advance();
            return true;
        }

        private void Advance()
        {
            _current = _lexer.NextToken();
        }

        private ExpressionNode? AddErrorExpression(string error)
        {
            AddError(error);
            Advance();
            return null;
        }

        private void AddError(string error)
        {
            if (!_errors.Contains(error, StringComparer.Ordinal))
            {
                _errors.Add(error);
            }
        }

        private void AddLexerErrors()
        {
            foreach (var error in _lexer.Errors)
            {
                AddError(error);
            }
        }
    }

    private sealed class Lexer
    {
        private readonly string _source;
        private readonly List<string> _errors = [];
        private int _position;

        public Lexer(string source)
        {
            _source = source;
        }

        public IReadOnlyList<string> Errors => _errors;

        public Token NextToken()
        {
            SkipWhitespace();

            if (_position >= _source.Length)
            {
                return new Token(TokenKind.End, string.Empty, _position);
            }

            var start = _position;
            var current = _source[_position];

            if (current == '\'')
            {
                return ReadString(start);
            }

            if (char.IsAsciiDigit(current))
            {
                return ReadNumber(start);
            }

            if (IsIdentifierStart(current))
            {
                return ReadIdentifier(start);
            }

            _position++;
            return current switch
            {
                '.' => new Token(TokenKind.Dot, ".", start),
                ',' => new Token(TokenKind.Comma, ",", start),
                '(' => new Token(TokenKind.LeftParen, "(", start),
                ')' => new Token(TokenKind.RightParen, ")", start),
                '!' when MatchNext('=') => new Token(TokenKind.BangEquals, "!=", start),
                '!' => new Token(TokenKind.Bang, "!", start),
                '=' when MatchNext('=') => new Token(TokenKind.EqualsEquals, "==", start),
                '<' when MatchNext('=') => new Token(TokenKind.LessThanOrEqual, "<=", start),
                '<' => new Token(TokenKind.LessThan, "<", start),
                '>' when MatchNext('=') => new Token(TokenKind.GreaterThanOrEqual, ">=", start),
                '>' => new Token(TokenKind.GreaterThan, ">", start),
                '&' when MatchNext('&') => new Token(TokenKind.AndAnd, "&&", start),
                '|' when MatchNext('|') => new Token(TokenKind.OrOr, "||", start),
                _ => UnknownToken(current, start)
            };
        }

        private Token ReadString(int start)
        {
            _position++;
            var value = new List<char>();

            while (_position < _source.Length)
            {
                var current = _source[_position];
                if (current == '\'')
                {
                    if (_position + 1 < _source.Length && _source[_position + 1] == '\'')
                    {
                        value.Add('\'');
                        _position += 2;
                        continue;
                    }

                    _position++;
                    return new Token(TokenKind.String, new string(value.ToArray()), start);
                }

                value.Add(current);
                _position++;
            }

            _errors.Add($"Unterminated string literal at position {start}.");
            return new Token(TokenKind.String, new string(value.ToArray()), start);
        }

        private Token ReadNumber(int start)
        {
            while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
            {
                _position++;
            }

            if (_position < _source.Length && _source[_position] == '.')
            {
                _position++;
                while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
                {
                    _position++;
                }
            }

            return new Token(TokenKind.Number, _source[start.._position], start);
        }

        private Token ReadIdentifier(int start)
        {
            while (_position < _source.Length && IsIdentifierPart(_source[_position]))
            {
                _position++;
            }

            var text = _source[start.._position];
            return text switch
            {
                "true" => new Token(TokenKind.True, text, start),
                "false" => new Token(TokenKind.False, text, start),
                "null" => new Token(TokenKind.Null, text, start),
                _ => new Token(TokenKind.Identifier, text, start)
            };
        }

        private Token UnknownToken(char current, int start)
        {
            _errors.Add($"Unexpected character '{current}' at position {start}.");
            return new Token(TokenKind.Unknown, current.ToString(), start);
        }

        private bool MatchNext(char expected)
        {
            if (_position >= _source.Length || _source[_position] != expected)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }
        }

        private static bool IsIdentifierStart(char value)
            => char.IsAsciiLetter(value) || value == '_';

        private static bool IsIdentifierPart(char value)
            => char.IsAsciiLetterOrDigit(value) || value is '_' or '-';
    }

    private sealed record Token(TokenKind Kind, string Text, int Position);

    private enum TokenKind
    {
        Unknown,
        End,
        Identifier,
        String,
        Number,
        True,
        False,
        Null,
        Dot,
        Comma,
        LeftParen,
        RightParen,
        EqualsEquals,
        BangEquals,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
        AndAnd,
        OrOr,
        Bang
    }
}
