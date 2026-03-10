# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x.x   | :white_check_mark: |

## Reporting a Vulnerability

We take security seriously. If you discover a security vulnerability in the Quality Gate plugin, please report it responsibly.

### How to Report

1. **Do NOT** create a public GitHub issue for security vulnerabilities
2. Send an email to the repository owner through GitHub's private contact feature
3. Or create a [private security advisory](https://github.com/GeiserX/jellyfin-quality-gate/security/advisories/new)

### What to Include

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

### Response Timeline

- **Initial Response**: Within 48 hours
- **Status Update**: Within 7 days
- **Resolution Target**: Within 30 days for critical issues

### Security Considerations

This plugin handles access control for media files. When deploying:

1. **Keep Jellyfin Updated**: Always run the latest stable version of Jellyfin
2. **Review Policies Regularly**: Audit your quality gate policies periodically
3. **Limit Admin Access**: Only trusted users should have plugin configuration access
4. **Monitor Logs**: Check Jellyfin logs for any unusual Quality Gate activity

## Acknowledgments

We appreciate responsible disclosure and will acknowledge security researchers who help improve this plugin's security.






