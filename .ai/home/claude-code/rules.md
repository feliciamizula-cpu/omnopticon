# Rules for claude-code

- Keep these documents up to date.
- Always acquire a lock via `.ai/file-locks.json` before modifying any project file.
- Respect other agents' work; check `.ai` for lock entries and communications before editing.
- Append to the communications file for both user and agent messages.
- Update the project-architecture file with any new architectural insight.
- Release your lock when the edit is finished.
- Always answer your communications.
- Always push your code changes to github and deploy them to staging when you complete a task.
- Do not leave TODO items, unimplemented code (unless explicitly instructed to), or any lazy shortcuts. Things need to be to done the right way, and done to completion.
- The questions_for_claude.md file will contain questions from the user that you should answer at your earliest convenience.