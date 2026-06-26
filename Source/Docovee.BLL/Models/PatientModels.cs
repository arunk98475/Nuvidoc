namespace Docovee.BLL.Models;

public class PatientRegisterRequest
{
    public Guid SessionKey { get; set; }
    public string FullName { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;
}

public class PatientRegisterResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
}

public class InsuranceCarrierDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
