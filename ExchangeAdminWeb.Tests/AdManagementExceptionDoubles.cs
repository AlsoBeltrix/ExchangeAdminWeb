namespace Microsoft.ActiveDirectory.Management;

// Test doubles for the directory exception types the retry classifier names.
//
// WHY THEY ARE DECLARED HERE, in the real assembly's namespace, rather than mocked:
// Comms10kReplaceWriter.IsRetryable matches on the exception's FULL TYPE NAME as a string,
// deliberately, because Microsoft.ActiveDirectory.Management ships with RSAT rather than NuGet
// and appears nowhere in this solution - not a package reference, not an assembly reference, not
// a using. A `catch (ADException)` in the app would be both a build risk on the CI agent and an
// architectural departure.
//
// A double with any other name would exercise the classifier against a string it will never see
// in production. Declaring the names the classifier actually matches is what makes these tests
// evidence rather than decoration. They are test-assembly-only and the app never sees them; if
// the real assembly is ever referenced, THESE MUST BE DELETED, and the resulting ambiguity error
// is the reminder.

/// <summary>Stands in for the base directory exception. The observed transient audit fault surfaces as exactly this.</summary>
public class ADException(string message = "a required audit event could not be generated for the operation")
    : System.Exception(message);

/// <summary>
/// Stands in for the fault an OVERSIZED request produced during measurement. Deliberately NOT
/// derived from <see cref="ADException"/>: verified against the live assembly, it derives
/// directly from System.Exception, which is what makes the two cleanly separable by type.
/// </summary>
public class ADServerDownException(string message = "the server is not operational")
    : System.Exception(message);
