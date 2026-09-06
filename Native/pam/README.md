# PAM policy

Install `hyprnetshell` as `/etc/pam.d/hyprnetshell`, owned by root and not writable by ordinary users. The supplied policy targets distributions that provide the `system-login` PAM stack. Package maintainers must adapt the included stack name to the distribution rather than falling back to an unrelated application's policy.

The lock screen authenticates the current UID only and supports one password prompt. PAM stacks requiring multiple secret prompts or interactive MFA fail closed.
