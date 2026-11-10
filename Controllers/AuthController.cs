using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TSP.Backend.Data;
using TSP.Backend.Models;

namespace TSP.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthController(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] UserAuthDto request)
    {
        var username = (request.Username ?? "").Trim();
        var password = request.Password ?? "";

        if (username.Length < 3 || username.Length > 32)
            return BadRequest(new { message = "Логін: від 3 до 32 символів." });
        if (password.Length < 4 || password.Length > 72) // 72 байти - межа BCrypt
            return BadRequest(new { message = "Пароль: від 4 до 72 символів." });

        if (await _context.Users.AnyAsync(u => u.Username == username))
            return BadRequest(new { message = "Користувач з таким іменем вже існує." });

        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        });
        await _context.SaveChangesAsync();
        return Ok(new { message = "Реєстрація успішна!" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] UserAuthDto request)
    {
        var username = (request.Username ?? "").Trim();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password ?? "", user.PasswordHash))
            return Unauthorized(new { message = "Невірний логін або пароль." });

        var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username)
            }),
            Expires = DateTime.UtcNow.AddHours(2),
            Issuer = _configuration["Jwt:Issuer"],
            Audience = _configuration["Jwt:Audience"],
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var handler = new JwtSecurityTokenHandler();
        return Ok(new { token = handler.WriteToken(handler.CreateToken(descriptor)) });
    }
}

public class UserAuthDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}