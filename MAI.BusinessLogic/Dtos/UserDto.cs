using System;
using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Dtos
{
    public class UserDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;

        /// <summary>Denumirea subdiviziunii (păstrat sub numele vechi pentru frontend).</summary>
        public string Department { get; set; } = string.Empty;

        public Guid? OrgUnitId { get; set; }

        /// <summary>Subdiviziunea condusă de utilizator, dacă este șef.</summary>
        public Guid? LedOrgUnitId { get; set; }
        public string? LedOrgUnitName { get; set; }

        public bool HasEmail { get; set; }
        public UserRole Role { get; set; }
        public bool IsActive { get; set; }
        public bool EmailConfirmed { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>True dacă respectivul cont este blocat acum de prea multe încercări eșuate.</summary>
        public bool IsLockedOut { get; set; }
        public DateTime? LockoutEndsAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }

    public class CreateUserDto
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;

        /// <summary>Subdiviziunea în care se încadrează contul. Opțională.</summary>
        public Guid? OrgUnitId { get; set; }

        public UserRole Role { get; set; } = UserRole.Utilizator;
    }

    /// <summary>PATCH /api/Users/{id}/org-unit - null = scoate din structură.</summary>
    public class ChangeOrgUnitDto
    {
        public Guid? OrgUnitId { get; set; }
    }

    public class ChangeRoleDto
    {
        public UserRole Role { get; set; }
    }

    // Folosit de PATCH /api/Auth/change-password
    public class ChangePasswordDto
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    // Folosit de POST /api/Users/{id}/reset-password (administrator)
    public class ResetPasswordDto
    {
        public string NewPassword { get; set; } = string.Empty;
    }

    public class LoginDto
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class AuthResponseDto
    {
        public string Token { get; set; } = string.Empty;
        public UserDto User { get; set; } = new();
    }
}