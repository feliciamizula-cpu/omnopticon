#!/usr/bin/env python3
"""
Menu-driven script to interact with the `opencode run` command.
Provides options for various types of prompts to send to a coding agent API.
"""

import os
import subprocess
import sys
from pathlib import Path


def clear_screen():
    """Clear the terminal screen."""
    os.system('cls' if os.name == 'nt' else 'clear')


def get_working_directory():
    """Prompt the user for a working directory, defaulting to the current directory."""
    default_dir = os.getcwd()
    print(f"Current working directory: {default_dir}")
    user_input = input("Enter working directory (leave blank to use current): ").strip()
    return Path(user_input) if user_input else Path(default_dir)


def run_opencode_command(prompt: str, work_dir: Path) -> str:
    """
    Execute the `opencode run` command with the given prompt and working directory.
    Returns the output or error message.
    """
    if not work_dir.exists():
        return f"Error: Working directory '{work_dir}' does not exist."

    try:
        result = subprocess.run(
            ["opencode", "run", "--dir", str(work_dir)],
            input=prompt,
            text=True,
            capture_output=True,
            check=True
        )
        return result.stdout or "No output returned."
    except subprocess.CalledProcessError as e:
        return f"Error: {e.stderr or 'Command failed with no error message.'}"
    except FileNotFoundError:
        return "Error: 'opencode' command not found. Ensure it is installed and in your PATH."


def prompt_for_script_execution():
    """Prompt the user for a script to execute."""
    script = input("Enter the script or command to execute: ").strip()
    if not script:
        return None
    return f"Execute the following script or command:\n{script}"


def prompt_for_filesystem_operation():
    """Prompt the user for a filesystem operation."""
    print("\nFilesystem Operations:")
    print("1. List files in a directory")
    print("2. Create a directory")
    print("3. Delete a file")
    print("4. Read a file")
    print("5. Custom operation")
    choice = input("Select an operation (1-5): ").strip()

    if choice == "1":
        path = input("Enter directory path: ").strip()
        return f"List all files in the directory: {path}"
    elif choice == "2":
        path = input("Enter directory path to create: ").strip()
        return f"Create the directory: {path}"
    elif choice == "3":
        path = input("Enter file path to delete: ").strip()
        return f"Delete the file: {path}"
    elif choice == "4":
        path = input("Enter file path to read: ").strip()
        return f"Read and display the contents of the file: {path}"
    elif choice == "5":
        op = input("Enter your custom filesystem operation: ").strip()
        return f"Perform the following filesystem operation:\n{op}"
    else:
        return None


def prompt_for_search():
    """Prompt the user for a search query."""
    print("\nSearch Types:")
    print("1. Local codebase search")
    print("2. Online search (e.g., documentation, Stack Overflow)")
    print("3. Custom search")
    choice = input("Select a search type (1-3): ").strip()

    if choice == "1":
        query = input("Enter search query for the local codebase: ").strip()
        return f"Search the local codebase for: {query}"
    elif choice == "2":
        query = input("Enter online search query: ").strip()
        return f"Perform an online search for: {query}"
    elif choice == "3":
        query = input("Enter your custom search query: ").strip()
        return f"Perform the following search:\n{query}"
    else:
        return None


def prompt_for_custom_query():
    """Prompt the user for a custom query."""
    query = input("Enter your custom query or prompt: ").strip()
    return query


def display_menu():
    """Display the main menu and return the user's choice."""
    clear_screen()
    print("OPENCODE PROMPT MENU")
    print("====================")
    print("1. Execute a script or command")
    print("2. Perform a filesystem operation")
    print("3. Run a search (local or online)")
    print("4. Enter a custom query")
    print("5. Exit")
    return input("Select an option (1-5): ").strip()


def main():
    work_dir = get_working_directory()
    
    while True:
        choice = display_menu()
        
        if choice == "1":
            prompt = prompt_for_script_execution()
        elif choice == "2":
            prompt = prompt_for_filesystem_operation()
        elif choice == "3":
            prompt = prompt_for_search()
        elif choice == "4":
            prompt = prompt_for_custom_query()
        elif choice == "5":
            print("Exiting...")
            break
        else:
            print("Invalid choice. Please try again.")
            input("Press Enter to continue...")
            continue
        
        if not prompt:
            print("No prompt entered. Returning to menu.")
            input("Press Enter to continue...")
            continue
        
        print(f"\nExecuting prompt:\n{prompt}\n")
        output = run_opencode_command(prompt, work_dir)
        print("Output:")
        print("-" * 40)
        print(output)
        print("-" * 40)
        input("\nPress Enter to return to the menu...")


if __name__ == "__main__":
    main()