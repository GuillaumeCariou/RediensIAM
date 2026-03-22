namespace RediensIAM.Exceptions;

public class NotFoundException(string message) : Exception(message);
public class ForbiddenException(string message) : Exception(message);
public class BadRequestException(string message) : Exception(message);
public class ConflictException(string message) : Exception(message);
public class RateLimitException(string message) : Exception(message);
public class UnauthorizedException(string message) : Exception(message);
