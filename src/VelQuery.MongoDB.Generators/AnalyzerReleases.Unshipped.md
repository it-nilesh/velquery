; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VQ2001 | VelQuery.MongoDB.Contracts | Error | Placeholder references a parameter not declared by the contract method
VQ2002 | VelQuery.MongoDB.Contracts | Warning | Contract method parameter not referenced by any placeholder
VQ2003 | VelQuery.MongoDB.Contracts | Error | Document type, member type or BSON attribute not supported by the generated serializer
VQ2004 | VelQuery.MongoDB.Contracts | Error | Two members map to the same BSON field
VQ2005 | VelQuery.MongoDB.Contracts | Error | Malformed JSON in a filter, update, sort, projection or pipeline
VQ2006 | VelQuery.MongoDB.Contracts | Error | Placeholder used as a key or operator
VQ2007 | VelQuery.MongoDB.Contracts | Error | Placeholder where the value would be code, an identifier or a constant
VQ2008 | VelQuery.MongoDB.Contracts | Error | Parameter type cannot be bound as a scalar BSON value
VQ2009 | VelQuery.MongoDB.Contracts | Error | Invalid or unsupported Extended JSON wrapper
VQ2010 | VelQuery.MongoDB.Contracts | Error | Contract type is not a non-generic partial class in partial containing types
VQ2011 | VelQuery.MongoDB.Contracts | Error | Contract does not declare exactly one partial method definition
VQ2012 | VelQuery.MongoDB.Contracts | Error | Invalid contract method signature
VQ2013 | VelQuery.MongoDB.Contracts | Error | Return type not supported for the operation or cardinality
VQ2014 | VelQuery.MongoDB.Contracts | Error | Invalid attribute argument or document shape (update without operators, invalid stage, write stage)
VQ2015 | VelQuery.MongoDB.Contracts | Error | Two method parameters bind the same placeholder
VQ2016 | VelQuery.MongoDB.Contracts | Error | Duplicate key in a JSON object
VQ2017 | VelQuery.MongoDB.Contracts | Error | Internal generator failure
