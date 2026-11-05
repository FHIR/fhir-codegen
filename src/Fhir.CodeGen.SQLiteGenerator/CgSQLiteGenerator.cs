using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Fhir.CodeGen.SQLiteGenerator;

[Generator]
public sealed class CgSQLiteGenerator : IIncrementalGenerator
{
    //private const string _joiner_0 = ",\r\n";
    //private const string _joiner_1 = ",\r\n    ";
    //private const string _joiner_2 = ",\r\n        ";
    private const string _line_2 = "\r\n        ";
    private const string _line_3 = "\r\n            ";
    private const string _line_4 = "\r\n                ";
    private const string _line_5 = "\r\n                    ";
    private const string _comma_line_2 = ",\r\n        ";
    private const string _comma_line_4 = ",\r\n                ";
    private const string _comma_line_5 = ",\r\n                    ";

    private enum CgGenCategory
    {
        Class,
        Record,
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#if DO_NOT_ATTACH_DEBUGGER
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            System.Diagnostics.Debugger.Launch();
        }
#endif

        // create a generated file with our attributes so the target project can use them
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource(
            "CgSQLiteGeneratorAttributes.g.cs",
            SourceText.From(GeneratorAttributes.CgAttributes, Encoding.UTF8)));

        context.RegisterPostInitializationOutput(ctx => ctx.AddSource(
            "CgFhirUtils.g.cs",
            SourceText.From(GeneratorFhirUtils.CgFhirUtils, Encoding.UTF8)));

        IncrementalValuesProvider<ClassDeclarationSyntax> cDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsSyntaxTargetClassDec(s),
                transform: static (ctx, _) => GetClassTargetForGeneration(ctx));

        IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> ivpClasses =
                context.CompilationProvider.Combine(cDeclarations.Collect());

        context.RegisterSourceOutput(ivpClasses, (spc, source) => Execute(source.Item1, source.Item2, spc));


        IncrementalValuesProvider<RecordDeclarationSyntax> rDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsSyntaxTargetRecordDec(s),
                transform: static (ctx, _) => GetRecordTargetForGeneration(ctx));

        IncrementalValueProvider<(Compilation, ImmutableArray<RecordDeclarationSyntax>)> ivpRecords =
                context.CompilationProvider.Combine(rDeclarations.Collect());

        context.RegisterSourceOutput(ivpRecords, (spc, source) => Execute(source.Item1, source.Item2, spc));
    }


    /// <summary>
    /// Determines if the given <see cref="SyntaxNode"/> is a target for generation.
    /// </summary>
    /// <param name="syntaxNode">The syntax node to evaluate.</param>
    /// <returns><c>true</c> if the syntax node is a class declaration with specific attributes; otherwise, <c>false</c>.</returns>
    public static bool IsSyntaxTargetClassDec(SyntaxNode syntaxNode)
    {
        return
            (syntaxNode is ClassDeclarationSyntax cDeclarationSyntax) &&
            (cDeclarationSyntax.AttributeLists.Count > 0) &&
            cDeclarationSyntax.AttributeLists.Any(al => al.Attributes.Any(a => GeneratorAttributes._cgClassAttributes.Contains(a.Name.ToString())));
    }


    /// <summary>
    /// Determines if the given <see cref="SyntaxNode"/> is a target for generation.
    /// </summary>
    /// <param name="syntaxNode">The syntax node to evaluate.</param>
    /// <returns><c>true</c> if the syntax node is a class declaration with specific attributes; otherwise, <c>false</c>.</returns>
    public static bool IsSyntaxTargetRecordDec(SyntaxNode syntaxNode)
    {
        return
            (syntaxNode is RecordDeclarationSyntax rDeclarationSyntax) &&
            (rDeclarationSyntax.AttributeLists.Count > 0) &&
            rDeclarationSyntax.AttributeLists.Any(al => al.Attributes.Any(a => GeneratorAttributes._cgClassAttributes.Contains(a.Name.ToString())));
    }


    /// <summary>
    /// Retrieves the target class declaration syntax for generation.
    /// </summary>
    /// <param name="context">The generator syntax context.</param>
    /// <returns>The class declaration syntax node.</returns>
    public static ClassDeclarationSyntax GetClassTargetForGeneration(GeneratorSyntaxContext context)
    {
        ClassDeclarationSyntax cDeclarationSyntax = (ClassDeclarationSyntax)context.Node;
        return cDeclarationSyntax;
    }

    /// <summary>
    /// Retrieves the target class declaration syntax for generation.
    /// </summary>
    /// <param name="context">The generator syntax context.</param>
    /// <returns>The record declaration syntax node.</returns>
    public static RecordDeclarationSyntax GetRecordTargetForGeneration(GeneratorSyntaxContext context)
    {
        RecordDeclarationSyntax rDeclarationSyntax = (RecordDeclarationSyntax)context.Node;
        return rDeclarationSyntax;
    }

    public void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes, SourceProductionContext context)
    {
        ILookup<string, ClassDeclarationSyntax> classLookup = classes.ToLookup(c => c.Identifier.Text);

        foreach (ClassDeclarationSyntax classSyntax in classes.Where(c => c.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == GeneratorAttributes._cgSQLiteTable))))
        {
            // Converting the class to a semantic model to access much more meaningful data.
            SemanticModel model = compilation.GetSemanticModel(classSyntax.SyntaxTree);

            // Parse to declared symbol, so you can access each part of code separately,
            // such as interfaces, methods, members, contructor parameters etc.
            ISymbol? symbol = model.GetDeclaredSymbol(classSyntax);

            if (symbol is null)
            {
                continue;
            }

            List<MemberDeclarationSyntax> members = [];

            string? btn = ((INamedTypeSymbol)symbol).BaseType?.Name;
            while (btn != null)
            {
                ClassDeclarationSyntax? btcs = classLookup[btn].FirstOrDefault();
                if (btcs is null)
                {
                    break;
                }

                members.AddRange(btcs.Members);
                btn = compilation.GetSemanticModel(btcs.SyntaxTree).GetDeclaredSymbol(btcs) is INamedTypeSymbol ints
                    ? ints.BaseType?.Name
                    : null;
            }

            members.AddRange(classSyntax.Members);

            execute(
                compilation,
                symbol,
                members,
                context,
                CgGenCategory.Class);
        }
    }

    public void Execute(
        Compilation compilation,
        ImmutableArray<RecordDeclarationSyntax> records,
        SourceProductionContext context)
    {
        ILookup<string, RecordDeclarationSyntax> recordLookup = records.ToLookup(c => c.Identifier.Text);

        foreach (RecordDeclarationSyntax recordSyntax in records.Where(c => c.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == GeneratorAttributes._cgSQLiteTable))))
        {
            // Converting the record to a semantic model to access much more meaningful data.
            SemanticModel model = compilation.GetSemanticModel(recordSyntax.SyntaxTree);

            // Parse to declared symbol, so you can access each part of code separately,
            // such as interfaces, methods, members, contructor parameters etc.
            ISymbol? symbol = model.GetDeclaredSymbol(recordSyntax);

            if (symbol is null)
            {
                continue;
            }

            List<MemberDeclarationSyntax> members = [];
            string? btn = ((INamedTypeSymbol)symbol).BaseType?.Name;
            while (btn != null)
            {
                RecordDeclarationSyntax? btcs = recordLookup[btn].FirstOrDefault();
                if (btcs is null)
                {
                    break;
                }

                members.AddRange(btcs.Members);
                btn = compilation.GetSemanticModel(btcs.SyntaxTree).GetDeclaredSymbol(btcs) is INamedTypeSymbol ints
                    ? ints.BaseType?.Name
                    : null;
            }

            members.AddRange(recordSyntax.Members);

            execute(
                compilation,
                symbol,
                members,
                context,
                CgGenCategory.Record);
        }
    }

    private record struct TableColInfoRec(
        string name,
        string propType,
        string shortRead,
        string readerDirective,
        bool isPrimaryKey,
        bool isIdentity,
        bool isNullable,
        bool isEnum,
        bool useJson,
        bool isArray,
        string? foreignTable,
        string? foreignColumn,
        string? foreignModelType);

    private void execute(
        Compilation compilation,
        ISymbol symbol,
        List<MemberDeclarationSyntax> members,
        SourceProductionContext context,
        CgGenCategory genCategory)
    {
        string className = symbol.Name;
        string? classNamespace = symbol.ContainingNamespace?.ToDisplayString();
        string? classAssembly = symbol.ContainingAssembly?.Name;

        ILookup<string?, AttributeData> symbolAttributeLookup = symbol.GetAttributes().ToLookup(a => a.AttributeClass?.Name);

        string tableName = symbolAttributeLookup.Contains(GeneratorAttributes._cgSQLiteTable)
            ? symbolAttributeLookup[GeneratorAttributes._cgSQLiteTable].First().ConstructorArguments.FirstOrDefault().Value?.ToString() ?? className
            : className;

        int? pkColIndex = null;
        string? pkColName = null;
        string? pkPropType = null;
        bool pkIsIdentity = false;
        bool anyColIsJson = false;

        List<string> createColLines = [];
        List<string> createFKLines = [];
        List<TableColInfoRec> tableColInfo = [];

        foreach (MemberDeclarationSyntax member in members)
        {
            // only process properties
            if (member is not PropertyDeclarationSyntax pds)
            {
                continue;
            }

            SemanticModel pModel = compilation.GetSemanticModel(pds.SyntaxTree);
            //ISymbol? pSymbol = pModel.GetDeclaredSymbol(pds);

            //if (pSymbol is null)
            //{
            //    continue;
            //}

            // check for ignore property
            if (pds.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == GeneratorAttributes._cgSQLiteIgnore)))
            {
                continue;
            }

            Type memberType = pds.GetType();
            string propName = pds.Identifier.ToString();

            TypeInfo propTypeInfo = pModel.GetTypeInfo(pds.Type);
            string propTypeName = pds.Type.ToString();
            INamedTypeSymbol? namedTypeSymbol = propTypeInfo.Type is INamedTypeSymbol ints ? ints : null;

            bool nullable = propTypeName.EndsWith("?") || (namedTypeSymbol?.NullableAnnotation == NullableAnnotation.Annotated);
            if (nullable)
            {
                propTypeName = propTypeName.Substring(0, propTypeName.Length - 1);
            }

            // check for primary key property
            bool isPrimaryKey = pds.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == GeneratorAttributes._cgSQLiteKey));
            if (isPrimaryKey)
            {
                pkColName = propName;
                pkPropType = propTypeName;
                pkIsIdentity = (propTypeName == "int" || propTypeName == "long");
            }

            if (isPrimaryKey)
            {
                if (pkColName is null)
                {
                    throw new Exception("Primary key column name is null");
                }

                if (pkColName != propName)
                {
                    throw new Exception("Primary key column name does not match property name");
                }

                pkColIndex = tableColInfo.Count;
            }

            // check for any kind of non-scalar type
            bool memberIsNonScalar = memberType.IsArray || (memberType.IsGenericType && typeof(IEnumerable<object>).IsAssignableFrom(memberType));

            // check for type nullability
            //bool nullable = Nullable.GetUnderlyingType(member.) != null;
            //bool nullable = new NullabilityInfoContext().Create(prop).WriteState == NullabilityState.Nullable;

            string? jsonTypeName = null;
            string? enumTypeName = null;
            bool memberIsEnum = false;

            if ((namedTypeSymbol != null) &&
                (
                    (namedTypeSymbol.TypeKind == TypeKind.Enum) ||
                    ((namedTypeSymbol.TypeArguments.Length != 0) && (namedTypeSymbol.TypeArguments[0].TypeKind == TypeKind.Enum)) ||
                    //((namedTypeSymbol.TypeKind == TypeKind.Struct) && (namedTypeSymbol.TypeArguments.Length != 0))
                    (namedTypeSymbol.TypeKind == TypeKind.Struct) && (namedTypeSymbol.TypeArguments.Length != 0) && !namedTypeSymbol.TypeArguments[0].ContainingNamespace.Name.StartsWith("System")
                    //(namedTypeSymbol.TypeKind == TypeKind.Struct) && (namedTypeSymbol.TypeArguments.Length != 0) && namedTypeSymbol.TypeArguments[0].ContainingNamespace.Name.StartsWith("HL7")
                ))
            {
                memberIsEnum = true;

                // grab the enum type name
                if (namedTypeSymbol.TypeKind == TypeKind.Enum)
                {
                    if (namedTypeSymbol.ContainingType is null)
                    {
                        enumTypeName = $"{namedTypeSymbol.ContainingNamespace}.{namedTypeSymbol.Name}";
                    }
                    else
                    {
                        enumTypeName = $"{namedTypeSymbol.ContainingType.ContainingNamespace}.{namedTypeSymbol.ContainingType.Name}.{namedTypeSymbol.Name}";
                    }
                }
                else if (namedTypeSymbol.TypeArguments.Length != 0)
                {
                    if (namedTypeSymbol.TypeArguments[0].ContainingType is null)
                    {
                        enumTypeName = $"{namedTypeSymbol.TypeArguments[0].ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].Name}";
                    }
                    else
                    {
                        enumTypeName = $"{namedTypeSymbol.TypeArguments[0].ContainingType.ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].ContainingType.Name}.{namedTypeSymbol.TypeArguments[0].Name}";
                    }
                }
                else
                {
                    enumTypeName = namedTypeSymbol.Name;
                }
            }
            else if (namedTypeSymbol != null)
            {
                if (namedTypeSymbol.TypeArguments.Length != 0)
                {
                    if (namedTypeSymbol.Name.Contains("List") ||
                        namedTypeSymbol.Name.Contains("Enumerable"))
                    {
                        memberIsNonScalar = true;
                        if (namedTypeSymbol.TypeArguments[0].ContainingType is null)
                        {
                            //jsonTypeName = $"System.Collections.Generic.List<{namedTypeSymbol.TypeArguments[0].ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].Name}>";
                            jsonTypeName = $"{namedTypeSymbol.TypeArguments[0].ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].Name}";
                        }
                        else
                        {
                            //jsonTypeName = $"System.Collections.Generic.List<{namedTypeSymbol.TypeArguments[0].ContainingType.ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].ContainingType.Name}.{namedTypeSymbol.TypeArguments[0].Name}>";
                            jsonTypeName = $"{namedTypeSymbol.TypeArguments[0].ContainingType.ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].ContainingType.Name}.{namedTypeSymbol.TypeArguments[0].Name}";
                        }
                    }
                    else
                    {
                        if (namedTypeSymbol.TypeArguments[0].ContainingType is null)
                        {
                            jsonTypeName = $"{namedTypeSymbol.TypeArguments[0].ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].Name}";
                        }
                        else
                        {
                            jsonTypeName = $"{namedTypeSymbol.TypeArguments[0].ContainingType.ContainingNamespace}.{namedTypeSymbol.TypeArguments[0].ContainingType.Name}.{namedTypeSymbol.TypeArguments[0].Name}";
                        }
                    }
                }
                else
                {
                    if (namedTypeSymbol.ContainingType is null)
                    {
                        jsonTypeName = $"{namedTypeSymbol.ContainingNamespace}.{namedTypeSymbol.Name}";
                    }
                    else
                    {
                        jsonTypeName = $"{namedTypeSymbol.ContainingType.ContainingNamespace}.{namedTypeSymbol.ContainingType.Name}.{namedTypeSymbol.Name}";
                    }
                }

                //if (nullable)
                //{
                //    jsonTypeName += "?";
                //}
            }

            bool useJson = !memberIsEnum && !_sqliteTypeMap.ContainsKey(propTypeName);

            // add our column line
            createColLines.Add(
                $"{pds.Identifier.ToString()} {getSqlType(propTypeName, memberIsEnum, useJson, memberIsNonScalar)}" +
                $"{(isPrimaryKey ? getPkDirective(pkPropType) : string.Empty)}" +
                $"{((nullable || isPrimaryKey) ? string.Empty : " NOT NULL")}");

            // check for foreign key property information
            string? foreignTable = null;
            string? foreignColumn = null;
            string? foreignModelType = null;
            foreach (AttributeListSyntax als in pds.AttributeLists)
            {
                foreach (AttributeSyntax a in als.Attributes.Where(a => a.Name.ToString() == GeneratorAttributes._cgSQLiteForeignKey))
                {
                    foreach (AttributeArgumentSyntax arg in a.ArgumentList?.Arguments ?? [])
                    {
                        switch (arg.NameEquals?.Name.ToString())
                        {
                            case "ReferenceTable":
                                foreignTable = arg.Expression.ToString();
                                break;

                            case "ReferenceColumn":
                                foreignColumn = arg.Expression.ToString();
                                break;

                            case "ModelTypeName":
                                foreignModelType = arg.Expression.ToString();
                                break;
                        }
                    }
                }
            }

            if ((foreignTable is not null) && (foreignColumn is not null))
            {
                createFKLines.Add($"FOREIGN KEY ({pds.Identifier}) REFERENCES {foreignTable}({foreignColumn})");
            }

            // create the select retrieval pair
            if (nullable && _sqliteNullableReadDirectives.TryGetValue(propTypeName, out string? readFormat))
            {
                tableColInfo.Add(new (
                    propName,
                    propTypeName,
                    string.Format(readFormat.Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count),
                    string.Format(readFormat, pds.Identifier.ToString(), "reader", tableColInfo.Count),
                    isPrimaryKey,
                    isPrimaryKey && (propTypeName == "int" || propTypeName == "long"),
                    nullable,
                    memberIsEnum,
                    useJson,
                    memberIsNonScalar,
                    foreignTable,
                    foreignColumn,
                    foreignModelType));
            }
            else if (!nullable && _sqliteReadDirectives.TryGetValue(propTypeName, out readFormat))
            {
                tableColInfo.Add(new (
                    propName,
                    propTypeName,
                    string.Format(readFormat.Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count),
                    string.Format(readFormat, pds.Identifier.ToString(), "reader", tableColInfo.Count),
                    isPrimaryKey,
                    isPrimaryKey && (propTypeName == "int" || propTypeName == "long"),
                    nullable,
                    memberIsEnum,
                    useJson,
                    memberIsNonScalar,
                    foreignTable,
                    foreignColumn,
                    foreignModelType));
            }
            else if (memberIsEnum)
            {
                //// build the reader directive for the enum type
                //string ef = $"Enum.TryParse(reader.GetString({tableColInfo.Count}), out {propName});";

                tableColInfo.Add(new (
                    propName,
                    propTypeName,
                    nullable
                        ? string.Format(_sqliteNullableReadDirectives["enum"].Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count, enumTypeName)
                        : string.Format(_sqliteReadDirectives["enum"].Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count, enumTypeName),
                    nullable
                        ? string.Format(_sqliteNullableReadDirectives["enum"], pds.Identifier.ToString(), "reader", tableColInfo.Count, enumTypeName)
                        : string.Format(_sqliteReadDirectives["enum"], pds.Identifier.ToString(), "reader", tableColInfo.Count, enumTypeName),
                    isPrimaryKey,
                    isPrimaryKey && (propTypeName == "int" || propTypeName == "long"),
                    nullable,
                    memberIsEnum,
                    useJson,
                    memberIsNonScalar,
                    foreignTable,
                    foreignColumn,
                    foreignModelType));
            }
            else if (memberIsNonScalar)
            {
                anyColIsJson = true;

                tableColInfo.Add(new(
                    pds.Identifier.ToString(),
                    propTypeName,
                    nullable
                        ? string.Format(_sqliteNullableReadDirectives["JSON[]"].Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName)
                        : string.Format(_sqliteReadDirectives["JSON[]"].Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName),
                    nullable
                        ? string.Format(_sqliteNullableReadDirectives["JSON[]"], pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName)
                        : string.Format(_sqliteReadDirectives["JSON[]"], pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName),
                    isPrimaryKey,
                    isPrimaryKey && (propTypeName == "int" || propTypeName == "long"),
                    nullable,
                    memberIsEnum,
                    useJson,
                    memberIsNonScalar,
                    foreignTable,
                    foreignColumn,
                    foreignModelType));
            }
            else
            {
                // tableColInfo.Add((
                //     pds.Identifier.ToString(),
                //     propTypeName,
                //     $"// ERROR: could not determine retrieval directive for type {propName}:{propTypeName}",
                //     $"// ERROR: could not determine retrieval directive for type {propName}:{propTypeName}",
                //     isPrimaryKey,
                //     isPrimaryKey && (propTypeName == "int" || propTypeName == "long"),
                //     nullable,
                //     memberIsEnum
                //     ));

                anyColIsJson = true;

                tableColInfo.Add(new(
                    pds.Identifier.ToString(),
                    propTypeName,
                    nullable
                        ? string.Format(_sqliteNullableReadDirectives["JSON"].Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName)
                        : string.Format(_sqliteReadDirectives["JSON"].Remove(0, 6), pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName),
                    nullable
                        ? string.Format(_sqliteNullableReadDirectives["JSON"], pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName)
                        : string.Format(_sqliteReadDirectives["JSON"], pds.Identifier.ToString(), "reader", tableColInfo.Count, jsonTypeName),
                    isPrimaryKey,
                    isPrimaryKey && (propTypeName == "int" || propTypeName == "long"),
                    nullable,
                    memberIsEnum,
                    useJson,
                    memberIsNonScalar,
                    foreignTable,
                    foreignColumn,
                    foreignModelType));
            }
        }

        string argFilterParams = string.Join(", ", getFnFilterParams(true, true));
        string argFilters = string.Join(", ", getFnFilterArgs(true, true));
        string conditionLinesUsingJoiner2 = string.Join(_line_2, getConditionLines(true, true, true, true));
        string colReaderDirectivesComma5 = string.Join(_comma_line_5, tableColInfo.Select(p => p.readerDirective));

        string genClassVars;
        if ((pkPropType == "int") || (pkPropType == "long"))
        {
            genClassVars = $$$""""
                    public static string DefaultTableName => "{{{tableName}}}";
                    internal static {{{pkPropType}}} _indexValue = 0;
                    public static {{{pkPropType}}} GetIndex() => Interlocked.Increment(ref _indexValue);
                """";
        }
        else
        {
            genClassVars = $"    public static string DefaultTableName => \"{tableName}\";";
        }

        string fnCreateTable = $$$""""
                public static bool CreateTable(IDbConnection dbConnection, string? dbTableName = null)
                {
                    dbTableName ??= "{{{tableName}}}";

                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"""
                        CREATE TABLE IF NOT EXISTS {dbTableName} (
                            {{{string.Join(_comma_line_4, [.. createColLines, .. createFKLines])}}}
                        )
                        """;

                    command.ExecuteNonQuery();

                    {{{string.Join(_line_2, getIndexLines())}}}

                    return true;
                }
            """";

        string fnDropTable = $$$""""
                public static bool DropTable(
                    IDbConnection dbConnection,
                    string? dbTableName = null)
                {
                    dbTableName ??= "{{{tableName}}}";
                    
                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"DROP TABLE IF EXISTS {dbTableName}";
                    
                    command.ExecuteNonQuery();
                    
                    return true;
                }
            """";

        string fnLoadMaxKey = $$$""""
                public static void LoadMaxKey(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    int defaultValue = 0)
                {
                    dbTableName ??= "{{{tableName}}}";

                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT MAX({{{(pkColName is null ? "ROWID" : pkColName)}}}) FROM {dbTableName}";

                    try
                    {
                        object? result = command.ExecuteScalar();
                        if (result is {{{(pkColName is null ? "int" : pkPropType)}}} value)
                        {
                            _indexValue = value;
                        }
                        else if (result is long l)
                        {
                            _indexValue = {{{((pkColName is null) || (pkPropType == "int") ? "Convert.ToInt32(l)" : "null")}}};
                        }
                    }
                    catch (Exception)
                    {
                        _indexValue = defaultValue;
                    }
                }
            """";

        string fnSelectMaxKey = $$$""""
                public static {{{(pkColName is null ? "int" : pkPropType)}}}? SelectMaxKey(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    int defaultValue = 0)
                {
                    dbTableName ??= "{{{tableName}}}";
            
                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT MAX({{{(pkColName is null ? "ROWID" : pkColName)}}}) FROM {dbTableName}";
            
                    object? result = command.ExecuteScalar();
                    if (result is {{{(pkColName is null ? "int" : pkPropType)}}} value)
                    {
                        return value;
                    }
                    else if (result is long l)
                    {
                        return {{{((pkColName is null) || (pkPropType == "int") ? "Convert.ToInt32(l)" : "null")}}};
                    }
            
                    return null;
                }
            """";

        string fnSelectSingle = $$$""""
                public static {{{className}}}? SelectSingle(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    bool orJoinConditions = false,
                    bool compareStringsWithLike = false,
                    {{{argFilterParams}}})
                {
                    dbTableName ??= "{{{tableName}}}";
            
                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT {{{string.Join(", ", tableColInfo.Select(p => p.name))}}} FROM {dbTableName}";
            
                    string joiner = orJoinConditions ? " OR " : " AND ";
                    string stringComparator = compareStringsWithLike ? " LIKE " : " = ";
                    bool addedCondition = false;
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
            
                    {{{conditionLinesUsingJoiner2}}}
            
                    using (IDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new()
                            {
                                {{{colReaderDirectivesComma5}}}
                            };
                        }
                    }
                    return null;
                }
            """";

        string fnSelectList = $$$""""
                public static List<{{{className}}}> SelectList(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    string[]? orderByProperties = null,
                    string? orderByDirection = null,
                    bool orJoinConditions = false,
                    bool compareStringsWithLike = false,
                    int? resultLimit = null,
                    int? resultOffset = null,
                    {{{argFilterParams}}})
                {
                    dbTableName ??= "{{{tableName}}}";

                    List<{{{className}}}> results = new();

                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT {{{string.Join(", ", tableColInfo.Select(p => p.name))}}} FROM {dbTableName}";

                    string joiner = orJoinConditions ? " OR " : " AND ";
                    string stringComparator = compareStringsWithLike ? " LIKE " : " = ";
                    bool addedCondition = false;
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}

                    {{{conditionLinesUsingJoiner2}}}

                    if ((orderByProperties != null) && (orderByProperties.Length > 0))
                    {
                        command.CommandText += $" ORDER BY {string.Join(", ", orderByProperties)}";
                        if (orderByDirection?.StartsWith("d", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            command.CommandText += $" DESC";
                        }
                        else
                        {
                            command.CommandText += $" ASC";
                        }
                    }

                    if (resultLimit.HasValue && (resultLimit.Value > 0))
                    {
                        command.CommandText += $" LIMIT {resultLimit.Value}";
                        if (resultOffset.HasValue && (resultOffset.Value > 0))
                        {
                            command.CommandText += $" OFFSET {resultOffset.Value}";
                        }
                    }

                    using (IDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new()
                            {
                                {{{colReaderDirectivesComma5}}}
                            });
                        }
                    }
                    return results;
                }
            """";
        
        string fnSelectEnumerable = $$$""""
                public static IEnumerable<{{{className}}}> SelectEnumerable(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    string[]? orderByProperties = null,
                    string? orderByDirection = null,
                    bool orJoinConditions = false,
                    bool compareStringsWithLike = false,
                    int? resultLimit = null,
                    int? resultOffset = null,
                    {{{argFilterParams}}})
                {
                    dbTableName ??= "{{{tableName}}}";
            
                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT {{{string.Join(", ", tableColInfo.Select(p => p.name))}}} FROM {dbTableName}";
            
                    string joiner = orJoinConditions ? " OR " : " AND ";
                    string stringComparator = compareStringsWithLike ? " LIKE " : " = ";
                    bool addedCondition = false;
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                                
                    {{{conditionLinesUsingJoiner2}}}
            
                    if ((orderByProperties != null) && (orderByProperties.Length > 0))
                    {
                        command.CommandText += $" ORDER BY {string.Join(", ", orderByProperties)}";
                        if (orderByDirection?.StartsWith("d", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            command.CommandText += $" DESC";
                        }
                        else
                        {
                            command.CommandText += $" ASC";
                        }
                    }

                    if (resultLimit.HasValue && (resultLimit.Value > 0))
                    {
                        command.CommandText += $" LIMIT {resultLimit.Value}";
                        if (resultOffset.HasValue && (resultOffset.Value > 0))
                        {
                            command.CommandText += $" OFFSET {resultOffset.Value}";
                        }
                    }
            
                    using (IDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            yield return new()
                            {
                                {{{colReaderDirectivesComma5}}}
                            };
                        }
                    }
            
                    yield break;
                }
            """";

        string fnSelectDict = $$$""""
                public static Dictionary<{{{(pkColName is null ? "int" : pkPropType)}}}, {{{className}}}> SelectDict(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    string[]? orderByProperties = null,
                    string? orderByDirection = null,
                    bool orJoinConditions = false,
                    bool compareStringsWithLike = false,
                    int? resultLimit = null,
                    int? resultOffset = null,
                    {{{argFilterParams}}})
                {
                    dbTableName ??= "{{{tableName}}}";

                    Dictionary<{{{(pkColName is null ? "int" : pkPropType)}}}, {{{className}}}> results = new();

                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT {{{string.Join(", ", tableColInfo.Select(p => p.name))}}} FROM {dbTableName}";

                    string joiner = orJoinConditions ? " OR " : " AND ";
                    string stringComparator = compareStringsWithLike ? " LIKE " : " = ";
                    bool addedCondition = false;
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}

                    {{{conditionLinesUsingJoiner2}}}

                    if ((orderByProperties != null) && (orderByProperties.Length > 0))
                    {
                        command.CommandText += $" ORDER BY {string.Join(", ", orderByProperties)}";
                        if (orderByDirection?.StartsWith("d", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            command.CommandText += $" DESC";
                        }
                        else
                        {
                            command.CommandText += $" ASC";
                        }
                    }

                    if (resultLimit.HasValue && (resultLimit.Value > 0))
                    {
                        command.CommandText += $" LIMIT {resultLimit.Value}";
                        if (resultOffset.HasValue && (resultOffset.Value > 0))
                        {
                            command.CommandText += $" OFFSET {resultOffset.Value}";
                        }
                    }

                    {{{(pkColName is null ? "int rowId = 0;" : string.Empty)}}}
                    using (IDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add({{{(pkColName is null ? "rowId++" : tableColInfo[(int)pkColIndex!].shortRead)}}}, new()
                            {
                                {{{colReaderDirectivesComma5}}}
                            });
                        }
                    }
                    return results;
                }
            """";

        string fnSelectCount = $$$""""
                public static int SelectCount(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    bool orJoinConditions = false,
                    bool compareStringsWithLike = false,
                    {{{argFilterParams}}})
                {
                    dbTableName ??= "{{{tableName}}}";
                    
                    IDbCommand command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT COUNT({{{(pkColName is null ? "*" : pkColName)}}}) FROM {dbTableName}";
                    
                    string joiner = orJoinConditions ? " OR " : " AND ";
                    string stringComparator = compareStringsWithLike ? " LIKE " : " = ";
                    bool addedCondition = false;
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                                        
                    {{{conditionLinesUsingJoiner2}}}

                    object? result = command.ExecuteScalar();
                    if (result is int value)
                    {
                        return value;
                    }
                    else if (result is long l)
                    {
                        return {{{((pkColName is null) || (pkPropType == "int") ? "Convert.ToInt32(l)" : "null")}}};
                    }

                    return -1;
                }
            """";

        string insertColsLine5 = string.Join(_comma_line_5, tableColInfo.Where(p => p.isIdentity == false).Select(p => p.name));
        string insertParamsLine5 = string.Join(_comma_line_5, tableColInfo.Where(p => p.isIdentity == false).Select(p => "$" + p.name));
        string insertColsWithIdentityLine5 = string.Join(_comma_line_5, tableColInfo.Select(p => p.name));
        string insertParamsWithIdentityLine5 = string.Join(_comma_line_5, tableColInfo.Select(p => "$" + p.name));

        string fnInsertSingle = $$$""""
                public static {{{(pkColName is null ? "void" : pkPropType)}}} Insert(
                    IDbConnection dbConnection,
                    {{{className}}} value,
                    string? dbTableName = null,
                    bool ignoreDuplicates = false,
                    bool insertPrimaryKey = false)
                {
                    dbTableName ??= "{{{tableName}}}";
                    {{{getNonIdentityPkInit(pkColName, pkPropType)}}}
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                    string insertLiteral = ignoreDuplicates ? "INSERT OR IGNORE" : "INSERT";

                    if (insertPrimaryKey)
                    {
                        using (IDbTransaction transaction = dbConnection.BeginTransaction())
                        {
                            IDbCommand command = dbConnection.CreateCommand();
                            command.CommandText = $"""
                                {insertLiteral} INTO {dbTableName} (
                                    {{{insertColsWithIdentityLine5}}}
                                ) VALUES (
                                    {{{insertParamsWithIdentityLine5}}}
                                );
                                """;
            
                            {{{string.Join(_line_4, getInsertCommandParamLines(true, null, pkPropType, includeIdentity: true, ignoreDupeProperty: "ignoreDuplicates"))}}}
            
                            transaction.Commit();
                        }
                    }
                    else
                    {
                        using (IDbTransaction transaction = dbConnection.BeginTransaction())
                        {
                            IDbCommand command = dbConnection.CreateCommand();
                            command.CommandText = $"""
                                {insertLiteral} INTO {dbTableName} (
                                    {{{insertColsLine5}}}
                                ) VALUES (
                                    {{{insertParamsLine5}}}
                                ) {{{(pkIsIdentity ? " RETURNING " + pkColName : string.Empty)}}};
                                """;
            
                            {{{string.Join(_line_4, getInsertCommandParamLines(true, pkIsIdentity ? pkColName : null, pkPropType, ignoreDupeProperty: "ignoreDuplicates"))}}}
            
                            transaction.Commit();
                        }
                    }
                    {{{(pkColName is null ? "return" : $"return value.{pkColName}")}}};
                }
            """";

        string fnInsertList = $$$""""
                public static void Insert(
                    IDbConnection dbConnection,
                    List<{{{className}}}> values,
                    string? dbTableName = null,
                    bool ignoreDuplicates = false,
                    bool insertPrimaryKey = false)
                {
                    dbTableName ??= "{{{tableName}}}";
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                    string insertLiteral = ignoreDuplicates ? "INSERT OR IGNORE" : "INSERT";

                    if (insertPrimaryKey)
                    {
                        using (IDbTransaction transaction = dbConnection.BeginTransaction())
                        {
                            IDbCommand command = dbConnection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = $"""
                                {insertLiteral} INTO {dbTableName} (
                                    {{{insertColsWithIdentityLine5}}}
                                ) VALUES (
                                    {{{insertParamsWithIdentityLine5}}}
                                );
                                """;

                            {{{string.Join(_line_4, getInsertCommandParamLines(false, null, pkPropType, createParameters: true, includeIdentity: true, ignoreDupeProperty: "ignoreDuplicates"))}}}
            
                            command.Prepare();
            
                            foreach ({{{className}}} value in values)
                            {
                                {{{string.Join(_line_5, getInsertCommandParamLines(false, null, pkPropType, instantiateParameters: true, executeCommand: true, includeIdentity: true, ignoreDupeProperty: "ignoreDuplicates"))}}}
                            }
            
                            transaction.Commit();
                        }
                    }
                    else
                    {
                        using (IDbTransaction transaction = dbConnection.BeginTransaction())
                        {
                            IDbCommand command = dbConnection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = $"""
                                {insertLiteral} INTO {dbTableName} (
                                    {{{insertColsLine5}}}
                                ) VALUES (
                                    {{{insertParamsLine5}}}
                                ) {{{(pkIsIdentity ? " RETURNING " + pkColName : string.Empty)}}};
                                """;
            
                            {{{string.Join(_line_4, getInsertCommandParamLines(false, pkIsIdentity ? pkColName : null, pkPropType, createParameters: true, ignoreDupeProperty: "ignoreDuplicates"))}}}
                        
                            command.Prepare();

                            foreach ({{{className}}} value in values)
                            {
                                {{{getNonIdentityPkInit(pkColName, pkPropType)}}}
                                {{{string.Join(_line_5, getInsertCommandParamLines(false, pkIsIdentity ? pkColName : null, pkPropType, instantiateParameters: true, executeCommand: true))}}}
                            }

                            transaction.Commit();
                        }
                    }
                }
            """";

        string fnInsertEnumerable = $$$""""
                public static void Insert(
                    IDbConnection dbConnection,
                    IEnumerable<{{{className}}}> values,
                    string? dbTableName = null,
                    bool ignoreDuplicates = false,
                    bool insertPrimaryKey = false)
                {
                    dbTableName ??= "{{{tableName}}}";
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                    string insertLiteral = ignoreDuplicates ? "INSERT OR IGNORE" : "INSERT";
            
                    if (insertPrimaryKey)
                    {
                        using (IDbTransaction transaction = dbConnection.BeginTransaction())
                        {
                            IDbCommand command = dbConnection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = $"""
                                {insertLiteral} INTO {dbTableName} (
                                    {{{insertColsWithIdentityLine5}}}
                                ) VALUES (
                                    {{{insertParamsWithIdentityLine5}}}
                                );
                                """;

                            {{{string.Join(_line_4, getInsertCommandParamLines(false, null, pkPropType, createParameters: true, includeIdentity: true, ignoreDupeProperty: "ignoreDuplicates"))}}}

                            command.Prepare();

                            foreach ({{{className}}} value in values)
                            {
                                {{{string.Join(_line_5, getInsertCommandParamLines(false, null, pkPropType, instantiateParameters: true, executeCommand: true, includeIdentity: true, setIdentity: false, ignoreDupeProperty: "ignoreDuplicates"))}}}
                            }

                            transaction.Commit();
                        }
                    }
                    else
                    {
                        using (IDbTransaction transaction = dbConnection.BeginTransaction())
                        {
                            IDbCommand command = dbConnection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = $"""
                                {insertLiteral} INTO {dbTableName} (
                                    {{{insertColsLine5}}}
                                ) VALUES (
                                    {{{insertParamsLine5}}}
                                ) {{{(pkIsIdentity ? " RETURNING " + pkColName : string.Empty)}}};
                                """;

                            {{{string.Join(_line_4, getInsertCommandParamLines(false, pkIsIdentity ? pkColName : null, pkPropType, createParameters: true, ignoreDupeProperty: "ignoreDuplicates"))}}}

                            command.Prepare();

                            foreach ({{{className}}} value in values)
                            {
                                {{{getNonIdentityPkInit(pkColName, pkPropType)}}}
                                {{{string.Join(_line_5, getInsertCommandParamLines(false, pkIsIdentity ? pkColName : null, pkPropType, instantiateParameters: true, executeCommand: true, setIdentity: false, ignoreDupeProperty: "ignoreDuplicates"))}}}
                            }

                            transaction.Commit();
                        }
                    }
                }
            """";

        string fnUpdateSingle = $$$""""
                public static {{{className}}} Update(
                    IDbConnection dbConnection,
                    {{{className}}} value,
                    string? dbTableName = null)
                {
                    dbTableName ??= "{{{tableName}}}";
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                                        
                    using (IDbTransaction transaction = dbConnection.BeginTransaction())
                    {
                        IDbCommand command = dbConnection.CreateCommand();
                        command.CommandText = $"""
                            UPDATE {dbTableName} SET
                                {{{string.Join(_comma_line_5, tableColInfo.Where(p => p.isPrimaryKey == false).Select(p => p.name + " = $" + p.name))}}}
                            WHERE
                                {{{pkColName}}} = ${{{pkColName}}}
                            """;
                    
                        {{{string.Join(_line_3, getInsertCommandParamLines(true, pkIsIdentity ? pkColName : null, pkPropType, includeIdentity: true, isInsert: false))}}}
                    
                        transaction.Commit();
                    }
                    
                    return value;
                }
            """";

        string fnUpdateEnumerable = $$$""""
                public static void Update(
                    IDbConnection dbConnection,
                    IEnumerable<{{{className}}}> values,
                    string? dbTableName = null)
                {
                    dbTableName ??= "{{{tableName}}}";
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}
                                                
                    using (IDbTransaction transaction = dbConnection.BeginTransaction())
                    {
                        IDbCommand command = dbConnection.CreateCommand();
                        command.CommandText = $"""
                            UPDATE {dbTableName} SET
                                {{{string.Join(_comma_line_5, tableColInfo.Where(p => p.isPrimaryKey == false).Select(p => p.name + " = $" + p.name))}}}
                            WHERE
                                {{{pkColName}}} = ${{{pkColName}}}
                            """;
                    
                        {{{string.Join(
                            _line_3,
                            getInsertCommandParamLines(
                                false,
                                pkIsIdentity ? pkColName : null,
                                pkPropType,
                                createParameters: true,
                                includeIdentity: true,
                                isInsert: false))}}}
                    
                        foreach ({{{className}}} value in values)
                        {
                            {{{string.Join(
                                _line_4,
                                getInsertCommandParamLines(
                                    false,
                                    pkIsIdentity ? pkColName : null,
                                    pkPropType,
                                    instantiateParameters: true,
                                    executeCommand: true,
                                    setIdentity: false,
                                    includeIdentity: true,
                                    isInsert: false))}}}
                        }
                    
                        transaction.Commit();
                    }
                }
            """";

        string deleteCommandParamsSingleLine3 = string.Join(
            _line_3,
            getInsertCommandParamLines(
                true,
                pkIsIdentity ? pkColName : null,
                pkPropType,
                createParameters: true,
                instantiateParameters: true,
                executeCommand: true,
                includeIdentity: true,
                identityOnly: true,
                setIdentity: false));

        string deleteCommandParamsLine3 = string.Join(
            _line_3,
            getInsertCommandParamLines(
                false,
                pkIsIdentity ? pkColName : null,
                pkPropType,
                createParameters: true,
                includeIdentity: true,
                identityOnly: true,
                setIdentity: false));

        string deleteCommandValuesLine4 = string.Join(
            _line_4,
            getInsertCommandParamLines(
                false,
                pkIsIdentity ? pkColName : null,
                pkPropType,
                instantiateParameters: true,
                executeCommand: true,
                setIdentity: false,
                includeIdentity: true,
                identityOnly: true));

        string fnDeleteSingle = $$$""""
                public static void Delete(IDbConnection dbConnection, {{{className}}} value, string? dbTableName = null)
                {
                    dbTableName ??= "{{{tableName}}}";
                                        
                    using (IDbTransaction transaction = dbConnection.BeginTransaction())
                    {
                        IDbCommand command = dbConnection.CreateCommand();
                        command.CommandText = $"DELETE FROM {dbTableName} WHERE {{{pkColName}}} = ${{{pkColName}}}";
                    
                        {{{deleteCommandParamsSingleLine3}}}

                        transaction.Commit();
                    }
                }
            """";

        string fnDeleteEnumerable = $$$""""
                public static void Delete(IDbConnection dbConnection, IEnumerable<{{{className}}}> values, string? dbTableName = null)
                {
                    dbTableName ??= "{{{tableName}}}";
                                        
                    using (IDbTransaction transaction = dbConnection.BeginTransaction())
                    {
                        IDbCommand command = dbConnection.CreateCommand();
                        command.CommandText = $"DELETE FROM {dbTableName} WHERE {{{pkColName}}} = ${{{pkColName}}}";
                                
                        {{{deleteCommandParamsLine3}}}
            
                        foreach ({{{className}}} value in values)
                        {
                            {{{deleteCommandValuesLine4}}}
                        }
            
                        transaction.Commit();
                    }
                }
            """";

        string fnDeleteFiltered = $$$""""
                public static void Delete(
                    IDbConnection dbConnection,
                    string? dbTableName = null,
                    bool orJoinConditions = false,
                    bool compareStringsWithLike = false,
                    {{{argFilterParams}}})
                {
                    dbTableName ??= "{{{tableName}}}";
                    {{{(anyColIsJson ? "string? dbJson;" : string.Empty)}}}

                    string joiner = orJoinConditions ? " OR " : " AND ";
                    string stringComparator = compareStringsWithLike ? " LIKE " : " = ";
                                        
                    using (IDbTransaction transaction = dbConnection.BeginTransaction())
                    {
                        IDbCommand command = dbConnection.CreateCommand();
                        command.CommandText = $"DELETE FROM {dbTableName}";
                                        
                        bool addedCondition = false;
                    
                        {{{conditionLinesUsingJoiner2}}}

                        command.ExecuteNonQuery();

                        transaction.Commit();
                    }
                }
            """";

        context.AddSource(
            $"{className}{"SQLite"}.g.cs",
            SourceText.From($$$""""
                    //------------------------------------------------------------------------------
                    // <auto-generated>
                    //     This code was generated by a tool.
                    //
                    //     Changes to this file may cause incorrect behavior and will be lost if
                    //     the code is regenerated.
                    // </auto-generated>
                    //------------------------------------------------------------------------------

                    #nullable enable

                    using System.Collections.Generic;
                    using System.Data;
                    using System.Text;
                    using System.Text.Json;
                    using Fhir.CodeGen.SQLiteGenerator;
                
                    namespace {{{classNamespace}}};
                
                    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
                    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
                    public partial {{{decForGenCategory(genCategory)}}} {{{className}}}
                    {
                    {{{genClassVars}}}

                    {{{fnCreateTable}}}

                    {{{fnDropTable}}}

                    {{{fnLoadMaxKey}}}

                    {{{fnSelectMaxKey}}}

                    {{{fnSelectSingle}}}

                    {{{fnSelectList}}}

                    {{{fnSelectEnumerable}}}

                    {{{fnSelectDict}}}

                    {{{fnSelectCount}}}

                    {{{fnInsertSingle}}}

                    {{{fnInsertList}}}

                    {{{fnInsertEnumerable}}}

                    {{{fnUpdateSingle}}}

                    {{{fnUpdateEnumerable}}}

                    {{{fnDeleteSingle}}}

                    {{{fnDeleteEnumerable}}}

                    {{{fnDeleteFiltered}}}

                    }

                    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
                    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
                    public static class {{{className}}}Extensions
                    {
                        public static {{{className}}}? SelectSingle<T>(
                            this IDbConnection dbCon,
                            string? dbTableName = null,
                            bool orJoinConditions = false,
                            bool compareStringsWithLike = false,
                            {{{argFilterParams}}})
                            where T : {{{className}}}
                        {
                            return {{{className}}}.SelectSingle(
                                dbCon,
                                dbTableName,
                                orJoinConditions,
                                compareStringsWithLike,
                                {{{argFilters}}});
                        }

                        public static List<{{{className}}}> SelectList<T>(
                            this IDbConnection dbCon,
                            string? dbTableName = null,
                            string[]? orderByProperties = null,
                            string? orderByDirection = null,
                            bool orJoinConditions = false,
                            bool compareStringsWithLike = false,
                            int? resultLimit = null,
                            int? resultOffset = null,
                            {{{argFilterParams}}})
                            where T : {{{className}}}
                        {
                            return {{{className}}}.SelectList(
                                dbCon,
                                dbTableName,
                                orderByProperties,
                                orderByDirection,
                                orJoinConditions,
                                compareStringsWithLike,
                                resultLimit,
                                resultOffset,
                                {{{argFilters}}});
                        }

                        public static IEnumerable<{{{className}}}> SelectEnumerable<T>(
                            this IDbConnection dbCon,
                            string? dbTableName = null,
                            string[]? orderByProperties = null,
                            string? orderByDirection = null,
                            bool orJoinConditions = false,
                            bool compareStringsWithLike = false,
                            int? resultLimit = null,
                            int? resultOffset = null,
                            {{{argFilterParams}}})
                            where T : {{{className}}}
                        {
                            return {{{className}}}.SelectEnumerable(
                                dbCon,
                                dbTableName,
                                orderByProperties,
                                orderByDirection,
                                orJoinConditions,
                                compareStringsWithLike,
                                resultLimit,
                                resultOffset,
                                {{{argFilters}}});
                        }
                    
                        public static int SelectCount<T>(
                            this IDbConnection dbCon,
                            string? dbTableName = null,
                            bool orJoinConditions = false,
                            bool compareStringsWithLike = false,
                            {{{argFilterParams}}})
                            where T : {{{className}}}
                        {
                            return {{{className}}}.SelectCount(
                                dbCon,
                                dbTableName,
                                orJoinConditions,
                                compareStringsWithLike,
                                {{{argFilters}}});
                        }
                                                            
                        public static void Insert(
                            this IDbConnection dbCon,
                            {{{className}}} value,
                            string? dbTableName = null,
                            bool ignoreDuplicates = false,
                            bool insertPrimaryKey = false)
                        {
                            {{{className}}}.Insert(
                                dbCon,
                                value,
                                dbTableName,
                                ignoreDuplicates,
                                insertPrimaryKey);
                        }

                        public static void Insert(
                            this IDbConnection dbCon,
                            List<{{{className}}}> values,
                            string? dbTableName = null,
                            bool ignoreDuplicates = false,
                            bool insertPrimaryKey = false)
                        {
                            {{{className}}}.Insert(
                                dbCon,
                                values,
                                dbTableName,
                                ignoreDuplicates,
                                insertPrimaryKey);
                        }

                        public static void Insert(
                            this IDbConnection dbCon,
                            IEnumerable<{{{className}}}> values,
                            string? dbTableName = null,
                            bool ignoreDuplicates = false,
                            bool insertPrimaryKey = false)
                        {
                            {{{className}}}.Insert(
                                dbCon,
                                values,
                                dbTableName,
                                ignoreDuplicates,
                                insertPrimaryKey);
                        }

                        public static void Update(this IDbConnection dbCon, {{{className}}} value, string? dbTableName = null)
                        {
                            {{{className}}}.Update(dbCon, value, dbTableName);
                        }

                        public static void Update(this IDbConnection dbCon, IEnumerable<{{{className}}}> values, string? dbTableName = null)
                        {
                            {{{className}}}.Update(dbCon, values, dbTableName);
                        }

                        public static void Delete(this IDbConnection dbCon, {{{className}}} value, string? dbTableName = null)
                        {
                            {{{className}}}.Delete(dbCon, value, dbTableName);
                        }
                    
                        public static void Delete(this IDbConnection dbCon, IEnumerable<{{{className}}}> values, string? dbTableName = null)
                        {
                            {{{className}}}.Delete(dbCon, values, dbTableName);
                        }

                        public static void Delete(
                            this IDbConnection dbCon,
                            string? dbTableName = null,
                            bool orJoinConditions = false,
                            bool compareStringsWithLike = false,
                            {{{argFilterParams}}})
                        {
                            {{{className}}}.Delete(dbCon, dbTableName, orJoinConditions, compareStringsWithLike, {{{argFilters}}});
                        }

                        public static void Insert(
                            this {{{className}}} value,
                            IDbConnection dbCon,
                            string? dbTableName = null,
                            bool ignoreDuplicates = false,
                            bool insertPrimaryKey = false)
                        {
                            {{{className}}}.Insert(
                                dbCon,
                                value,
                                dbTableName,
                                ignoreDuplicates,
                                insertPrimaryKey);
                        }
                    
                        public static void Insert(
                            this List<{{{className}}}> values,
                            IDbConnection dbCon,
                            string? dbTableName = null,
                            bool ignoreDuplicates = false,
                            bool insertPrimaryKey = false)
                        {
                            {{{className}}}.Insert(
                                dbCon,
                                values,
                                dbTableName,
                                ignoreDuplicates,
                                insertPrimaryKey);
                        }

                        public static void Insert(
                            this IEnumerable<{{{className}}}> values,
                            IDbConnection dbCon,
                            string? dbTableName = null,
                            bool ignoreDuplicates = false,
                            bool insertPrimaryKey = false)
                        {
                            {{{className}}}.Insert(
                                dbCon,
                                values,
                                dbTableName,
                                ignoreDuplicates,
                                insertPrimaryKey);
                        }
                    
                        public static void Update(this {{{className}}} value, IDbConnection dbCon, string? dbTableName = null)
                        {
                            {{{className}}}.Update(dbCon, value, dbTableName);
                        }
                    
                        public static void Update(this IEnumerable<{{{className}}}> values, IDbConnection dbCon, string? dbTableName = null)
                        {
                            {{{className}}}.Update(dbCon, values, dbTableName);
                        }

                        public static void Delete(this {{{className}}} value, IDbConnection dbCon, string? dbTableName = null)
                        {
                            {{{className}}}.Delete(dbCon, value, dbTableName);
                        }
                    
                        public static void Delete(this IEnumerable<{{{className}}}> values, IDbConnection dbCon, string? dbTableName = null)
                        {
                            {{{className}}}.Delete(dbCon, values, dbTableName);
                        }
                    }

                    #nullable restore
                    """"
            , Encoding.UTF8)
        );

        return;

        string decForGenCategory(CgGenCategory genCategory) => genCategory switch
        {
            CgGenCategory.Class => "class",
            CgGenCategory.Record => "record class",
            _ => "class",
        };

        string getPkDirective(string? pkTypeName) => pkTypeName switch
        {
            // note that in SQLite, an INTEGER PRIMARY KEY uses the internal ROWID automatically, so no IDENTITY declaration is necessary
            "int" => " PRIMARY KEY",            // " IDENTITY(1,1) PRIMARY KEY",
            "long" => " PRIMARY KEY",           // " IDENTITY(1,1) PRIMARY KEY",
            _ => " PRIMARY KEY",
        };

        string getNonIdentityPkInit(string? pkColName, string? pkTypeName) => pkTypeName switch
        {
            "guid" => $"value.{pkColName} = Guid.NewGuid();",
            _ => string.Empty,
        };

        IEnumerable<string> getIndexLines()
        {
            if (!symbolAttributeLookup.Contains(GeneratorAttributes._cgSQLiteIndex))
            {
                yield break;
            }

            // generate any indexes
            foreach (AttributeData ad in symbolAttributeLookup[GeneratorAttributes._cgSQLiteIndex])
            {
                string[] columns = ad.ConstructorArguments
                    .FirstOrDefault()
                    .Values
                    .Select(tc => tc.Value?.ToString() ?? string.Empty)
                    .Where(v => !string.IsNullOrEmpty(v)).
                    ToArray();

                if (columns.Length == 0)
                {
                    continue;
                }

                string indexName = $"IDX_{{dbTableName}}_{(string.Join("_", columns))}";

                yield return "command = dbConnection.CreateCommand();";
                yield return "command.CommandText = $\"\"\"";
                yield return $"    CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{{dbTableName}}\" (";
                yield return $"        {string.Join(", ", columns.Select(v => $"\"{v}\""))}";
                yield return "    )";
                yield return "    \"\"\";";
                yield return "command.ExecuteNonQuery();";
                yield return string.Empty;
            }
        }

        IEnumerable<string> getFnFilterParams(bool includeNullFilter, bool allowKeysIn)
        {
            foreach (TableColInfoRec rec in tableColInfo)
            {
                yield return $"{rec.propType}? {rec.name} = null";

                if ((!rec.isPrimaryKey) && (!rec.isIdentity))
                {
                    switch (rec.propType)
                    {
                        case "int":
                        case "long":
                        case "decimal":
                        case "double":
                            yield return $"string {rec.name}Operator = \"=\"";
                            break;
                    }
                }

                if (rec.isNullable)
                {
                    yield return $"bool? {rec.name}IsNull = null";
                }

                if (allowKeysIn && (!rec.isArray) && rec.name.EndsWith("Key"))
                {
                    yield return $"List<{rec.propType}>? {rec.name}Values = null";
                }
            }
        }

        IEnumerable<string> getFnFilterArgs(bool includeNullFilter, bool allowKeysIn)
        {
            foreach (TableColInfoRec rec in tableColInfo)
            {
                yield return rec.name;

                if ((!rec.isPrimaryKey) && (!rec.isIdentity))
                {
                    switch (rec.propType)
                    {
                        case "int":
                        case "long":
                        case "decimal":
                        case "double":
                            yield return $"{rec.name}Operator";
                            break;
                    }
                }

                if (rec.isNullable)
                {
                    yield return $"{rec.name}IsNull";
                }

                if (allowKeysIn && (!rec.isArray) && rec.name.EndsWith("Key"))
                {
                    yield return $"{rec.name}Values";
                }
            }
        }

        IEnumerable<string> getConditionLines(bool includeNullFilter, bool allowsOrJoining, bool allowsStringLike, bool allowKeysIn)
        {
            foreach (TableColInfoRec rec in tableColInfo)
            {
                yield return $"if ({rec.name} != null)";
                yield return "{";

                string conditionComparator;

                if (rec.isPrimaryKey || rec.isIdentity)
                {
                    conditionComparator = "=";
                }
                else
                {
                    conditionComparator = rec.propType switch
                    {
                        "int" => $$$"""{{{{rec.name}}}Operator}""",
                        "long" => $$$"""{{{{rec.name}}}Operator}""",
                        "decimal" => $$$"""{{{{rec.name}}}Operator}""",
                        "double" => $$$"""{{{{rec.name}}}Operator}""",
                        "string" => allowsStringLike ? "{stringComparator}" : "=",
                        _ => "="
                    };
                }

                if (allowsOrJoining)
                {
                    yield return $$$"""    command.CommandText += (addedCondition ? $" {joiner} " : " WHERE ") + $"{{{rec.name}}} {{{conditionComparator}}} ${{{rec.name}}}";""";
                }
                else
                {
                    yield return $$$"""    command.CommandText += (addedCondition ? " AND " : " WHERE ") + $"{{{rec.name}}} {{{conditionComparator}}} ${{{rec.name}}}";""";
                }

                yield return "    addedCondition = true;";
                yield return string.Empty;
                yield return $"    IDbDataParameter {rec.name}Param = command.CreateParameter();";
                yield return $"    {rec.name}Param.ParameterName = \"${rec.name}\";";

                if (rec.isEnum)
                {
                    yield return $"    {rec.name}Param.Value = {rec.name}.ToString();";
                }
                else if (rec.useJson)
                {
                    //yield return $"    {name}Param.Value = JsonSerializer.Serialize({name});";
                    //if (rec.isArray)
                    //{
                    //    if (rec.isNullable)
                    //    {
                    //        yield return $"    {rec.name}Param.Value = CgFhirUtils.SerializeArrayForDb({rec.name}, true);";
                    //    }
                    //    else
                    //    {
                    //        yield return $"    {rec.name}Param.Value = CgFhirUtils.SerializeArrayForDb({rec.name}, false);";
                    //    }
                    //}
                    //else
                    //{
                        if (rec.isNullable)
                        {
                            yield return $"    {rec.name}Param.Value = CgFhirUtils.TrySerializeForDb({rec.name}, out dbJson) ? dbJson : DBNull.Value;";
                        }
                        else
                        {
                            yield return $"    {rec.name}Param.Value = CgFhirUtils.TrySerializeForDb({rec.name}, out dbJson) ? dbJson : string.Empty;";
                        }
                    //}
                }
                else
                {
                    yield return $"    {rec.name}Param.Value = {rec.name};";
                }

                yield return $"    command.Parameters.Add({rec.name}Param);";
                yield return "}";
                yield return string.Empty;

                if (includeNullFilter && rec.isNullable)
                {
                    yield return $"if ({rec.name}IsNull == true)";
                    yield return "{";

                    if (allowsOrJoining)
                    {
                        yield return $$$"""    command.CommandText += (addedCondition ? $" {joiner} " : " WHERE ") + "{{{rec.name}}} IS NULL";""";
                    }
                    else
                    {
                        yield return $$$"""    command.CommandText += (addedCondition ? " AND " : " WHERE ") + "{{{rec.name}}} IS NULL";""";
                    }

                    yield return "    addedCondition = true;";
                    yield return "}";
                    yield return $"else if ({rec.name}IsNull == false)";
                    yield return "{";

                    if (allowsOrJoining)
                    {
                        yield return $$$"""    command.CommandText += (addedCondition ? $" {joiner} " : " WHERE ") + "{{{rec.name}}} IS NOT NULL";""";
                    }
                    else
                    {
                        yield return $$$"""    command.CommandText += (addedCondition ? " AND " : " WHERE ") + "{{{rec.name}}} IS NOT NULL";""";
                    }

                    yield return "    addedCondition = true;";
                    yield return "}";

                    yield return string.Empty;
                }

                if (allowKeysIn && (!rec.isArray) && rec.name.EndsWith("Key"))
                {
                    yield return $"if (({rec.name}Values is not null) && ({rec.name}Values.Count != 0))";
                    yield return "{";

                    if (allowsOrJoining)
                    {
                        yield return $$$"""    command.CommandText += (addedCondition ? $" {joiner} " : " WHERE ") + $"{{{rec.name}}} IN ";""";
                    }
                    else
                    {
                        yield return $$$"""    command.CommandText += (addedCondition ? " AND " : " WHERE ") + $"{{{rec.name}}} IN ";""";
                    }

                    yield return "    addedCondition = true;";
                    yield return "    List<string> vParamNames = new();";
                    yield return string.Empty;

                    yield return $"    for (int vIndex = 0; vIndex < {rec.name}Values.Count; vIndex++)";
                    yield return "    {";
                    yield return $$$"""        string vParamName = "{{{rec.name}}}Param" + vIndex.ToString();""";
                    yield return """        vParamNames.Add("@" + vParamName);""";
                    yield return string.Empty;
                    yield return $"        IDbDataParameter vp = command.CreateParameter();";
                    yield return $"        vp.ParameterName = vParamName;";
                    yield return $"        vp.Value = {rec.name}Values[vIndex];";
                    yield return $"        command.Parameters.Add(vp);";
                    yield return "    }";
                    yield return string.Empty;

                    yield return $$$"""    command.CommandText += "(" + string.Join(',', vParamNames) + ")";""";
                    yield return "}";
                }
            }
        }

        IEnumerable<string> getInsertCommandParamLines(
            bool singleValue,
            string? identityColName,
            string? identityColType,
            bool? createParameters = null,
            bool? instantiateParameters = null,
            bool? executeCommand = null,
            bool setIdentity = true,
            bool includeIdentity = false,
            bool identityOnly = false,
            bool isInsert = true,
            string? ignoreDupeProperty = null)
        {
            // if no specific type is specified, default to true
            if ((createParameters is null) && (instantiateParameters is null) && (executeCommand is null))
            {
                createParameters = true;
                instantiateParameters = true;
                executeCommand = true;
            }

            foreach (TableColInfoRec rec in tableColInfo)
            {
                // do not insert identity key values
                if (rec.isIdentity && !includeIdentity)
                {
                    continue;
                }
                else if (identityOnly && !rec.isIdentity)
                {
                    continue;
                }

                if (createParameters == true)
                {
                    yield return $"IDbDataParameter {rec.name}Param = command.CreateParameter();";

                    yield return $"{rec.name}Param.ParameterName = \"${rec.name}\";";
                    yield return $"command.Parameters.Add({rec.name}Param);";
                }
                
                if (instantiateParameters == true)
                {
                    if (rec.isNullable == true)
                    {
                        if (rec.isEnum)
                        {
                            yield return $"{rec.name}Param.Value = (value.{rec.name} is null) ? DBNull.Value : value.{rec.name}.ToString();";
                        }
                        else if (rec.useJson)
                        {
                            //yield return $"{rec.name}Param.Value = (value.{rec.name} is null) ? DBNull.Value : JsonSerializer.Serialize(value.{rec.name});";
                            //if (rec.isArray)
                            //{
                            //    yield return $"{rec.name}Param.Value = ((value.{rec.name} is null) || !value.{rec.name}.Any()) ? DBNull.Value : CgFhirUtils.SerializeArrayForDb(value.{rec.name}, true);";
                            //}
                            //else
                            //{
                                //yield return $"{rec.name}Param.Value = (value.{rec.name} is null) ? DBNull.Value : (CgFhirUtils.SerializeForDb(value.{rec.name}, true) ?? DBNull.Value);";
                                yield return $"{rec.name}Param.Value = CgFhirUtils.TrySerializeForDb(value.{rec.name}, out dbJson) ? dbJson : DBNull.Value;";
                            //}

                        }
                        else
                        {
                            yield return $"{rec.name}Param.Value = (value.{rec.name} is null) ? DBNull.Value : value.{rec.name};";
                        }
                    }
                    else
                    {
                        if (rec.isEnum)
                        {
                            yield return $"{rec.name}Param.Value = value.{rec.name}.ToString();";
                        }
                        else if (rec.useJson)
                        {
                            //yield return $"{rec.name}Param.Value = JsonSerializer.Serialize(value.{rec.name});";

                            //if (rec.isArray)
                            //{
                            //    yield return $"{rec.name}Param.Value = CgFhirUtils.SerializeArrayForDb(value.{rec.name}, false);";
                            //}
                            //else
                            //{
                                //yield return $"{rec.name}Param.Value = CgFhirUtils.SerializeForDb(value.{rec.name}, false);";
                                yield return $"{rec.name}Param.Value = CgFhirUtils.TrySerializeForDb(value.{rec.name}, out dbJson) ? dbJson : string.Empty;";
                            //}
                        }
                        else
                        {
                            yield return $"{rec.name}Param.Value = value.{rec.name};";
                        }
                    }
                }

                if (createParameters == true)
                {
                    // add an empty line between parameters
                    yield return string.Empty;
                }
            }

            if (executeCommand == true)
            {
                if ((identityColName is null) || (!setIdentity) || (!isInsert))
                {
                    if (ignoreDupeProperty is null)
                    {
                        yield return "int rowsAffected = command.ExecuteNonQuery();";
                        yield return "if (rowsAffected == 0) throw new Exception(\"Command failed!\");";
                    }
                    else
                    {
                        yield return "int rowsAffected = command.ExecuteNonQuery();";
                        yield return $"if (!{ignoreDupeProperty} && (rowsAffected == 0)) throw new Exception(\"Command failed!\");";
                    }
                }
                else
                {
                    if (ignoreDupeProperty == null)
                    {
                        yield return "object? commandResult = command.ExecuteScalar();";
                        yield return "if (commandResult is null) throw new Exception(\"Command failed!\");";

                        switch (identityColType)
                        {
                            case "int":
                                yield return $"value.{identityColName} = Convert.ToInt32(commandResult);";
                                break;
                            case "long":
                                yield return $"value.{identityColName} = Convert.ToInt64(commandResult);";
                                break;
                            default:
                                yield return $"value.{identityColName} = ({identityColType})commandResult;";
                                break;
                        }
                    }
                    else
                    {
                        yield return "object? commandResult = command.ExecuteScalar();";
                        yield return $"if (!{ignoreDupeProperty} && (commandResult is null)) throw new Exception(\"Command failed!\");";

                        switch (identityColType)
                        {
                            case "int":
                                yield return $"if (commandResult != null) value.{identityColName} = Convert.ToInt32(commandResult);";
                                break;
                            case "long":
                                yield return $"if (commandResult != null) value.{identityColName} = Convert.ToInt64(commandResult);";
                                break;
                            default:
                                yield return $"if (commandResult != null) value.{identityColName} = ({identityColType})commandResult;";
                                break;
                        }
                    }

                    //yield return "object? commandResult = command.ExecuteScalar();";
                    //yield return "if (commandResult is null) throw new Exception(\"Command failed!\");";

                    //switch (identityColType)
                    //{
                    //    case "int":
                    //        yield return $"value.{identityColName} = Convert.ToInt32(commandResult);";
                    //        break;
                    //    case "long":
                    //        yield return $"value.{identityColName} = Convert.ToInt64(commandResult);";
                    //        break;
                    //    default:
                    //        yield return $"value.{identityColName} = ({identityColType})commandResult;";
                    //        break;
                    //}
                }
            }
        }
    }

    /// <summary>
    /// Gets the SQL type for a given type.
    /// </summary>
    /// <remarks>
    /// Explicitly fetch the 'enum' type for anything that is an enum so we don't have to worry about indexing *all* the various enum types
    /// </remarks>
    private static string getSqlType(string type, bool isEnum = false, bool useJson = false, bool isArray = false)
    {
        if (isEnum)
        {
            return _sqliteTypeMap["enum"];
        }

        if (useJson)
        {
            return isArray
                ? _sqliteTypeMap["JSON[]"]
                : _sqliteTypeMap["JSON"];
        }

        return _sqliteTypeMap.TryGetValue(type, out string? name) ? name : "TEXT";
    }

    private static Dictionary<string, string> _sqliteReadDirectives = new()
    {
        { "bool", "{0} = {1}.GetBoolean({2})" },
        { "byte", "{0} = {1}.GetByte({2})" },
        { "byte[]", "{0} = {1}.GetBytes({2})" },
        { "char", "{0} = {1}.GetChar({2})" },
        { "char[]", "{0} = {1}.GetChars({2})" },
        { "DateTime", "{0} = {1}.GetDateTime({2})" },
        { "DateTimeOffset", "{0} = new DateTimeOffset({1}.GetDateTime({2}))" },
        { "Decimal", "{0} = {1}.GetDecimal({2})" },
        { "double", "{0} = {1}.GetDouble({2})" },
        { "enum", "{0} = Enum.Parse<{3}>({1}.GetString({2}))" },
        { "float", "{0} = {1}.GetFloat({2})" },
        { "Guid", "{0} = {1}.GetGuid({2})" },
        { "short", "{0} = {1}.GetInt16({2})" },
        { "int", "{0} = {1}.GetInt32({2})" },
        { "long", "{0} = {1}.GetInt64({2})" },
        { "sbyte", "(sbyte){0} = {1}.GetByte({2})" },
        { "string", "{0} = {1}.GetString({2})" },
        { "TimeSpan", "{0} = {1}.GetTimeSpan({2})" },
        { "ushort", "(ushort){0} = {1}.GetInt16({2})" },
        { "uint", "(uint){0} = {1}.GetInt32({2})" },
        { "ulong", "(ulong){0} = {1}.GetInt64({2})" },
        { "Uri", "{0} = new Uri({1}.GetString({2}))" },
        { "JSON", "{0} = CgFhirUtils.ParseFromDb<{3}>({1}.GetString({2})) ?? new {3}()" },
        //{ "JSON", "{0} = CgFhirUtils.ParseFromDb({1}.GetString({2}), typeof({3})) ?? new {3}()" },
        { "JSON[]", "{0} = CgFhirUtils.ParseArrayFromDb<{3}>({1}.GetString({2})) ?? new List<{3}>()" },
        //{ "JSON[]", "{0} = CgFhirUtils.ParseArrayFromDb({1}.GetString({2}), typeof({3})) ?? new List<{3}>()" },
    };

    private static Dictionary<string, string> _sqliteNullableReadDirectives = new()
    {
        { "bool", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetBoolean({2})" },
        { "bool?", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetBoolean({2})" },
        { "byte", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetByte({2})" },
        { "byte[]", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetBytes({2})" },
        { "char", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetChar({2})" },
        { "char[]", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetChars({2})" },
        { "DateTime", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetDateTime({2})" },
        { "DateTimeOffset", "{0} = {1}.IsDBNull({2}) ? null : new DateTimeOffset({1}.GetDateTime({2}))" },
        { "Decimal", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetDecimal({2})" },
        { "double", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetDouble({2})" },
        { "enum", "{0} = {1}.IsDBNull({2}) ? null : Enum.Parse<{3}>({1}.GetString({2}))" },
        { "float", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetFloat({2})" },
        { "Guid", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetGuid({2})" },
        { "short", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetInt16({2})" },
        { "int", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetInt32({2})" },
        { "int?", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetInt32({2})" },
        { "long", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetInt64({2})" },
        { "sbyte", "(sbyte){0} = {1}.IsDBNull({2}) ? null : {1}.GetByte({2})" },
        { "string", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetString({2})" },
        { "TimeSpan", "{0} = {1}.IsDBNull({2}) ? null : {1}.GetTimeSpan({2})" },
        { "ushort", "(ushort){0} = {1}.IsDBNull({2}) ? null : {1}.GetInt16({2})" },
        { "uint", "(uint){0} = {1}.IsDBNull({2}) ? null : {1}.GetInt32({2})" },
        { "ulong", "(ulong){0} = {1}.IsDBNull({2}) ? null : {1}.GetInt64({2})" },
        { "Uri", "{0} = {1}.IsDBNull({2}) ? null : new Uri({1}.GetString({2}))" },
        { "JSON", "{0} = {1}.IsDBNull({2}) ? null : CgFhirUtils.ParseFromDb<{3}>({1}.GetString({2}))" },
        //{ "JSON", "{0} = {1}.IsDBNull({2}) ? null : CgFhirUtils.ParseFromDb({1}.GetString({2}), typeof({3}))" },
        { "JSON[]", "{0} = {1}.IsDBNull({2}) ? null : CgFhirUtils.ParseArrayFromDb<{3}>({1}.GetString({2}))" },
        //{ "JSON[]", "{0} = {1}.IsDBNull({2}) ? null : CgFhirUtils.ParseArrayFromDb({1}.GetString({2}), typeof({3}))" },
    };

    // Mapping pulled from https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/types
    private static Dictionary<string, string> _sqliteTypeMap = new()
    {
        { "bool", "INTEGER" },
        { "bool?", "INTEGER" },
        { "byte", "INTEGER" },
        { "byte[]", "BLOB" },
        { "char", "TEXT" },
        { "char[]", "TEXT" },
        { "DateTime", "TEXT" },
        { "DateTimeOffset", "TEXT" },
        { "Decimal", "TEXT" },
        { "double", "REAL" },
        { "enum", "TEXT" },
        { "float", "REAL" },
        { "Guid", "TEXT" },
        { "short", "INTEGER" },
        { "int", "INTEGER" },
        { "int?", "INTEGER" },
        { "long", "INTEGER" },
        { "sbyte", "INTEGER" },
        { "string", "TEXT" },
        { "TimeSpan", "TEXT" },
        { "ushort", "INTEGER" },
        { "uint", "INTEGER" },
        { "ulong", "INTEGER" },
        { "Uri", "TEXT" },
        { "JSON", "TEXT" },
        { "JSON[]", "TEXT" },
    };

}
