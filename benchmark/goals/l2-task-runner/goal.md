Build `taskr`, a build task runner, as a .NET 10 console program in `src/Taskr` over a class library in `src/Taskr.Core`, with
unit tests in `tests/Taskr.Tests`. It reads `taskr.json` in the current folder (or the file given with `-f`): an object of
tasks by name, each with an optional `command` (run through the system shell, `sh -c` or `cmd /c`, in the file's folder),
`deps` (the names of tasks to run first), `inputs` and `outputs` (glob patterns relative to the file's folder, where `**`
matches any number of folders) and `env` (variables added for its command). `taskr <task>...` runs the tasks with their
dependencies, each once, in an order that respects them; tasks whose dependencies are done run at once, at most `-j N` at a
time (the number of processors by default). A task with inputs and outputs is skipped, printing `<name>: up to date`, when
its outputs exist and the hash of its inputs' paths and contents, its command and its `env` is the one recorded in
`.taskr/state.json` after its last success. Each line a command writes is printed with the prefix `[<name>] `. When a command
fails, no new task starts, the running ones finish, the run prints `<name> failed with exit code <n>` and exits with code 1,
and the failed task is not recorded as done. An unknown task, a dependency that does not exist or a cycle (printed as
`cycle: a -> b -> a`) is reported before anything runs, with exit code 2. `taskr --list` prints each task's name and
dependencies, `taskr --dry-run <task>` prints the tasks it would run in order without running them, and `taskr --clean`
removes `.taskr`.
