namespace KidTime.Server.Services.Dns;

/// <summary>
/// The household's DNS companion could not be read completely.
///
/// It exists so that a read which fails cannot be mistaken for a read which says the household
/// filters nothing. Those two look alike from a distance - both end with no block lists and no
/// site groups - and telling a parent their filter is off while it is running is the worse of the
/// two by far, because it is a confident answer rather than a missing one.
/// </summary>
public sealed class DnsCompanionException(string message) : Exception(message);
