# FHIR Sanitization Utils Documentation

The `FhirSanitizationUtils` class provides utilities for sanitizing and converting FHIR (Fast Healthcare Interoperability Resources) strings into various naming conventions while handling special characters, reserved words, and encoding issues.

## Overview

This utility class is designed to:
- Convert FHIR identifiers and values into programming-language-friendly formats
- Handle Unicode characters and special symbols
- Apply different naming conventions (PascalCase, camelCase, kebab-case, etc.)
- Sanitize strings for use in code generation and property names

## Main Sanitization Methods

### `SanitizeForProperty`

The primary method for sanitizing strings to be used as property names or identifiers in code.

**Parameters:**
- `value`: The input string to sanitize
- `reservedWords`: Optional set of language-reserved words to avoid
- `convertToConvention`: Target naming convention (default: None)
- `replacements`: Custom character replacement dictionary (default: underscore replacements)
- `checkForGmt`: Whether to handle GMT timezone formatting

**Process:**
1. Handles common URL prefixes
2. Normalizes Unicode characters
3. Applies character replacements
4. Removes duplicate underscores
5. Trims leading/trailing underscores
6. Applies naming convention
7. Adds "VAL_" prefix if needed for reserved words or invalid identifiers

### `SanitizeForCode`

Extracts a code name and value from input, typically for enumeration generation.

**Parameters:**
- `input`: The input string
- `reservedWords`: Set of reserved words to avoid
- `name` (out): The sanitized name for code use
- `value` (out): The original value

### `SanitizeForQuoted`

Sanitizes strings for use in quoted contexts (JSON, HTML, etc.).

**Parameters:**
- `input`: The input string
- `escapeToHtml`: Whether to use HTML entities instead of escape sequences
- `condenseWhitespace`: Whether to collapse multiple whitespace characters

### `SanitizeForValue`

Handles specific encoding bug fixes found in FHIR R5 release and basic value cleanup.

### `SanitizeToAscii`

Removes all non-printable ASCII characters from the input string.

## Character Replacement Tables

The class uses two main replacement dictionaries for handling special characters:

### Underscore Replacements (`ReplacementsWithUnderscores`)

Used when converting to underscore-delimited formats:

| Characters | Replacement | Description |
|------------|-------------|-------------|
| `…` | `_ellipsis_` | Ellipsis character |
| `...` | `_ellipsis_` | Three dots |
| `'` | `_quote_` | Single quote (smart) |
| `''` | `_double_quote_` | Double smart quote |
| `'''` | `_triple_quote_` | Triple smart quote |
| `'` | `_quote_` | Single quote (straight) |
| `''` | `_double_quote_` | Double straight quote |
| `'''` | `_triple_quote_` | Triple straight quote |
| `=` | `_equals_` | Equals sign |
| `==` | `_double_equals_` | Double equals |
| `===` | `_triple_equals_` | Triple equals |
| `!=` | `_not_equal_` | Not equal |
| `!~` | `_not_equivalent_` | Not equivalent |
| `<>` | `_not_equal_` | Not equal (alternate) |
| `<=` | `_less_or_equal_` | Less than or equal |
| `<` | `_less_than_` | Less than |
| `>=` | `_greater_or_equal_` | Greater than or equal |
| `>` | `_greater_than_` | Greater than |
| `!` | `_not_` | Exclamation/not |
| `*` | `_asterisk_` | Asterisk |
| `^` | `_power_` | Caret/power |
| `#` | `_number_` | Hash/number sign |
| `$` | `_dollar_` | Dollar sign |
| `%` | `_percent_` | Percent sign |
| `&` | `_and_` | Ampersand |
| `@` | `_at_` | At symbol |
| `+` | `_plus_` | Plus sign |
| `{`, `}`, `[`, `]`, `(`, `)` | `_` | Brackets and parentheses |
| `\`, `/` | `_` | Slashes |
| `|` | `_or_` | Pipe (or) |
| `||` | `_or_` | Double pipe (or) |
| `:`, `;`, `,` | `_` | Punctuation |
| `°` | `_degrees_` | Degree symbol |
| `?` | `_question_` | Question mark |
| `"`, `"`, `"` | `_quotation_` | Various quotation marks |
| `""`, `""`, `""` | `_double_quotation_` | Double quotation marks |
| ` `, `-`, `–`, `—`, `_`, `.` | `_` | Whitespace and separators |
| `\r`, `\n` | `` | Line breaks (removed) |
| `~` | `_tilde_` | Tilde |

### Pascal Case Replacements (`ReplacementsPascal`)

Used when converting to PascalCase or similar formats:

| Characters | Replacement | Description |
|------------|-------------|-------------|
| `…` | `Ellipsis` | Ellipsis character |
| `...` | `Ellipsis` | Three dots |
| `'` | `Quote` | Single quote (smart) |
| `''` | `DoubleQuote` | Double smart quote |
| `'''` | `TripleQuote` | Triple smart quote |
| `'` | `Quote` | Single quote (straight) |
| `''` | `DoubleQuote` | Double straight quote |
| `'''` | `TripleQuote` | Triple straight quote |
| `=` | `Equals` | Equals sign |
| `==` | `DoubleEquals` | Double equals |
| `===` | `TripleEquals` | Triple equals |
| `!=` | `NotEqual` | Not equal |
| `!~` | `NotEquivalent` | Not equivalent |
| `<>` | `NotEqual` | Not equal (alternate) |
| `<=` | `LessOrEqual` | Less than or equal |
| `<` | `LessThan` | Less than |
| `>=` | `GreaterOrEqual` | Greater than or equal |
| `>` | `GreaterThan` | Greater than |
| `!` | `Not` | Exclamation/not |
| `*` | `Asterisk` | Asterisk |
| `^` | `Power` | Caret/power |
| `#` | `Number` | Hash/number sign |
| `$` | `Dollar` | Dollar sign |
| `%` | `Percent` | Percent sign |
| `&` | `And` | Ampersand |
| `@` | `At` | At symbol |
| `+` | `Plus` | Plus sign |
| `{`, `}`, `[`, `]`, `(`, `)` | `_` | Brackets and parentheses |
| `\`, `/` | `_` | Slashes |
| `|` | `Or` | Pipe (or) |
| `||` | `Or` | Double pipe (or) |
| `:`, `;`, `,` | `_` | Punctuation |
| `°` | `Degrees` | Degree symbol |
| `?` | `Question` | Question mark |
| `"`, `"`, `"` | `Quotation` | Various quotation marks |
| `""`, `""`, `""` | `DoubleQuotation` | Double quotation marks |
| ` `, `-`, `–`, `—`, `_`, `.` | `_` | Whitespace and separators |
| `\r`, `\n` | `` | Line breaks (removed) |
| `~` | `Tilde` | Tilde |

## URL Prefix Handling

The sanitizer automatically handles common FHIR-related URL prefixes:

| URL Prefix | Replacement |
|------------|-------------|
| `http://hl7.org/fhir/` | `FHIR_` |
| `http://hl7.org/fhirpath/` | `FHIRPath_` |
| `http://terminology.hl7.org/` | `THO_` |
| `http://hl7.org/` | `HL7_` |
| `https://` | (removed) |
| `http://` | (removed) |
| `urn:oid:` | `OID_` |
| `urn:uuid:` | `UUID_` |
| `/` (at start) | `Per` |

## Naming Conventions Supported

- `FhirDotNotation`: Maintains FHIR dot notation
- `PascalDotNotation`: PascalCase with dots preserved
- `PascalCase`: Standard PascalCase
- `CamelCase`: Standard camelCase
- `UpperCase`: ALL_UPPERCASE
- `LowerCase`: all_lowercase
- `LowerKebab`: kebab-case
- `PascalDelimited`: PascalCase with custom delimiter
- `None`: No conversion applied

## Examples

### Basic Property Sanitization

```csharp
// Input: "Patient.name"
// Output: "Patient_name" (with underscores)
// Output: "PatientName" (PascalCase)

// Input: "http://hl7.org/fhir/Patient"
// Output: "FHIR_Patient"

// Input: "value[x]"
// Output: "value_x_" (with underscores)
// Output: "ValueX" (PascalCase)
```

### Special Character Handling

```csharp
// Input: "temperature≥37°C"
// Output: "temperature_greater_or_equal_37_degrees_C" (with underscores)
// Output: "TemperatureGreaterOrEqual37DegreesC" (PascalCase)

// Input: "dose!=null"
// Output: "dose_not_equal_null" (with underscores)
// Output: "DoseNotEqualNull" (PascalCase)
```

### Reserved Word Handling

```csharp
// Input: "class" (if "class" is in reserved words)
// Output: "VAL_class"

// Input: "_123invalid"
// Output: "VAL__123invalid"
```

### Value Sanitization Fixes

The `SanitizeForValue` method handles specific encoding issues:

```csharp
// Input: "SC#o TomC) and PrC-ncipe dobra"
// Output: "São Tomé and Príncipe dobra"

// Input: "Icelandic krC3na"
// Output: "Icelandic króna"
```

## Regular Expressions Used

The class uses compiled regular expressions for performance:

- **Duplicate Underscores**: `__+` - Removes multiple consecutive underscores
- **Duplicate Whitespace**: `\s+` - Condenses multiple whitespace characters
- **ASCII Escaping**: `[^ -~]+` - Matches non-printable ASCII characters

## Usage Notes

1. **Character Processing Order**: The sanitizer processes 3-character sequences first, then 2-character, then single characters
2. **Unicode Normalization**: Input is normalized using `NormalizationForm.FormD` before processing
3. **Reserved Words**: When a sanitized value matches a reserved word, it's prefixed with "VAL_"
4. **Leading Characters**: If the result starts with underscore or non-letter, "VAL_" is prepended
5. **GMT Handling**: Special handling for timezone strings containing "/GMT-" patterns
