using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Microsoft.Extensions.Logging;

namespace Fhir.CodeGen.Comparison.Outcomes;

public class OutcomeGenerator
{
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;


    public OutcomeGenerator(
        ComparisonDatabase db,
        ILoggerFactory loggerFactory)
        : this(db.DbConnection, loggerFactory)
    {
    }

    internal OutcomeGenerator(
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OutcomeGenerator>();

        _db = db;
    }

    public void GenerateOutcomes(
        bool processValueSets = true,
        bool processStructures = true,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        if (processStructures)
        {
            UnsafeCodeInputStore.RequireInputs(_db);
        }

        // ensure out tables exist and are empty
        DbOutcomeClasses.DropTables(_db, forValueSets: processValueSets, forStructures: processStructures);
        DbOutcomeClasses.CreateTables(_db, forValueSets: processValueSets, forStructures: processStructures);

        if (processValueSets)
        {
            ValueSetOutcomeGenerator vsGenerator = new(_db, _loggerFactory);
            vsGenerator.CreateOutcomesForValueSets(maxStepSize: maxStepSize, specificPairs: specificPairs);
        }

        if (processStructures)
        {
            StructureOutcomeGenerator sdGenerator = new(_db, _loggerFactory);
            sdGenerator.CreateOutcomesForStructures(maxStepSize: maxStepSize, specificPairs: specificPairs);
        }
    }
}
