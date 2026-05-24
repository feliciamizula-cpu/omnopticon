#!/usr/bin/env python3
"""
'a' - Quick access wrapper for opencode with interactive prompting.
Installs itself as 'a' alias for easy access to opencode's capabilities.
"""

import os
import subprocess
import sys
from pathlib import Path


def get_script_path():
    """Get the absolute path to this script."""
    return Path(__file__).resolve()


def install_alias():
    """Install the 'a' alias to shell configuration."""
    script_path = get_script_path()
    shell_configs = {
        'bash': ['.bashrc', '.bash_profile'],
        'zsh': ['.zshrc'],
        'fish': ['.config/fish/config.fish'],
    }
    
    alias_cmd = f"alias a='python3 {script_path}'"
    fish_alias = f"alias a 'python3 {script_path}'"
    
    installed = False
    shell = os.environ.get('SHELL', 'bash')
    
    if 'fish' in shell:
        config_path = Path.home() / shell_configs['fish'][0]
        if config_path.exists():
            content = config_path.read_text()
            if alias_cmd not in content and fish_alias not in content:
                config_path.write_text(f"{content}\n{fish_alias}\n")
                print(f"✓ Added 'a' alias to {config_path}")
                installed = True
            else:
                print(f"✓ 'a' alias already exists in {config_path}")
                installed = True
    else:
        for config in shell_configs.get('bash' if 'bash' in shell else 'zsh', []):
            config_path = Path.home() / config
            if config_path.exists():
                content = config_path.read_text()
                if alias_cmd not in content:
                    config_path.write_text(f"{content}\n{alias_cmd}\n")
                    print(f"✓ Added 'a' alias to {config_path}")
                    installed = True
                    break
                else:
                    print(f"✓ 'a' alias already exists in {config_path}")
                    installed = True
                    break
    
    if not installed:
        print("Could not find shell config. Add manually:")
        print(f"  alias a='python3 {script_path}'")
    
    print("\nRun 'source ~/.bashrc' (or appropriate file) or restart your terminal.")
    print("Then use 'a' to run opencode interactively.")


def run_opencode(user_input):
    """Run opencode with the user's input and system prompt."""
    
    system_prompt = """You are a helpful assistant for a software engineer. 
Provide detailed, technically accurate answers with code examples when appropriate.
If the request can be fulfilled with a shell script or Python script, provide the complete script.
If it's a command or operation that should be executed, provide clear instructions or a script to run.
Be concise but thorough - engineers value precision and efficiency."""

    try:
        result = subprocess.run(
            ['opencode', 'run', '--system', system_prompt, '--', user_input],
            capture_output=False,
            text=True,
        )
        return result.returncode == 0
    except FileNotFoundError:
        print("Error: 'opencode' command not found. Is it installed?")
        return False
    except Exception as e:
        print(f"Error running opencode: {e}")
        return False


def interactive_mode():
    """Run in interactive mode - prompt user for input."""
    try:
        user_input = input("\n🔧 Enter your request: ").strip()
        if user_input:
            run_opencode(user_input)
        else:
            print("\nNo input provided. Use 'a --help' for usage information.")
    except KeyboardInterrupt:
        print("\n")
        sys.exit(0)
    except EOFError:
        sys.exit(0)


def main():
    if len(sys.argv) > 1:
        if sys.argv[1] in ['--install', '-i']:
            install_alias()
        elif sys.argv[1] in ['--help', '-h']:
            print("""Usage: a [OPTIONS] [DIRECT_INPUT]

'a' - Quick access to opencode for software engineers

Options:
  -i, --install    Install the 'a' alias to your shell configuration
  -h, --help       Show this help message

Without options, 'a' prompts for input and sends it to opencode.
With direct input, 'a' sends that input directly to opencode.

Examples:
  a                    # Interactive mode - prompts for input
  a "how do I..."      # Direct mode - runs immediately
  a -i                 # Install alias
""")
        else:
            # Direct input mode
            user_input = ' '.join(sys.argv[1:])
            run_opencode(user_input)
    else:
        # Interactive mode
        interactive_mode()


if __name__ == '__main__':
    main()
