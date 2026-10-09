package officina

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io/fs"
	"maps"
	"slices"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf8"
)

// MemoryStore is where memory files are kept. Every operation is within one scope, which never sees another's
// files, and a store refuses with an error any scope that ValidMemoryScope rejects and any path that is not valid
// scope parts joined by "/", so no path leaves its scope. Directories are implied by the files' paths.
//
// It has four methods, more than an interface here usually has: the fewest the memory tool's commands need.
type MemoryStore interface {
	// List returns every file of the scope, in any order; none for a scope never written to.
	List(ctx context.Context, scope string) ([]MemoryFile, error)
	// Read returns the file's text, or an error that wraps fs.ErrNotExist when there is no such file.
	Read(ctx context.Context, scope, path string) (string, error)
	// Write creates the file, or replaces its text.
	Write(ctx context.Context, scope, path, text string) error
	// Delete deletes the file; it does nothing when there is none.
	Delete(ctx context.Context, scope, path string) error
}

// MemoryFile is a memory file: its path within the scope, parts separated by "/", and its size in bytes.
type MemoryFile struct {
	Path string
	Size int64
}

// The memory tool's limits, in characters.
const (
	// maxMemoryFile is the most a memory file may hold; a command that would exceed it is refused.
	maxMemoryFile = 50_000
	// maxMemoryView is the most a view shows, as the model's tool description says; ranges show the rest.
	maxMemoryView = 16_000
	// maxMemoryPath is the longest path.
	maxMemoryPath = 1_024
)

// memoryRoot is the memory directory as the model sees it.
const memoryRoot = "/memories"

// memoryDescription and memorySchema are what a provider without a native memory tool would send; they are part of
// the prefix, written as the .NET implementation writes them, so both fingerprint an agent with memory alike.
const (
	memoryDescription = "Your memory: a directory of text files under /memories that persists across " +
		"conversations. Commands: view (a file, or a directory's listing), create (create or overwrite a file), " +
		"str_replace, insert, delete and rename."
	memorySchema = `{"type":"object","properties":{"command":{"type":"string","enum":["view","create","str_replace","insert","delete","rename"]},` + "\n" +
		`"path":{"type":"string"},"view_range":{"type":"array","items":{"type":"integer"}},"file_text":{"type":"string"},` + "\n" +
		`"old_str":{"type":"string"},"new_str":{"type":"string"},"insert_line":{"type":"integer"},"insert_text":{"type":"string"},` + "\n" +
		`"old_path":{"type":"string"},"new_path":{"type":"string"}},"required":["command"]}`
)

// NewMemoryTool returns memory as a tool named "memory", with the commands of Claude's memory tool (view, create,
// str_replace, insert, delete, rename) over files under /memories, kept in store under the run's memory scope
// (RunOptions.MemoryScope). It is a write tool, so every call is audited before it runs; with NeedsApproval set,
// the commands that change memory need approval, and views never do. A provider with a memory tool of its own, which
// its model is trained on, sends that in its place. Its name, schema and handler must be left as they are.
func NewMemoryTool(store MemoryStore) Tool {
	return Tool{
		Name: "memory", Description: memoryDescription, InputSchema: jsontext.Value(memorySchema), Kind: Write,
		Handler: func(ctx context.Context, input jsontext.Value) (string, error) {
			scope, _ := ctx.Value(memoryScopeKey{}).(string)
			return runMemory(ctx, store, scope, input)
		},
		memory: true,
	}
}

// IsMemory reports whether t is the memory tool of NewMemoryTool.
func (t Tool) IsMemory() bool {
	return t.memory
}

// memoryScopeKey is the context key of the run's memory scope, which the memory tool reads.
type memoryScopeKey struct{}

// ValidMemoryScope reports whether scope is one a memory store accepts. A scope, like each part of a memory path, is
// never empty, "." or "..", is at most 255 characters, never ends with a dot or space, holds no control character
// and none of \ / : * ? " < > | %, and is no reserved Windows device name (CON, COM1, CONIN$…). So a path is
// relative, cannot climb out, and names the same file in every store.
func ValidMemoryScope(scope string) bool {
	n := utf8.RuneCountInString(scope)
	if n == 0 || n > 255 || !utf8.ValidString(scope) || strings.HasSuffix(scope, ".") ||
		strings.HasSuffix(scope, " ") || strings.ContainsAny(scope, `\/:*?"<>|%`) ||
		strings.ContainsFunc(scope, unicode.IsControl) {
		return false
	}
	base, _, _ := strings.Cut(scope, ".")
	base = strings.ToUpper(strings.TrimRightFunc(base, unicode.IsSpace))
	switch base {
	case "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$":
		return false
	}
	digit, found := strings.CutPrefix(base, "COM")
	if !found {
		digit, found = strings.CutPrefix(base, "LPT")
	}
	return !found || utf8.RuneCountInString(digit) != 1 || !strings.ContainsAny(digit, "0123456789¹²³")
}

// validMemoryPath reports whether path is valid scope parts joined by "/".
func validMemoryPath(path string) bool {
	if path == "" || utf8.RuneCountInString(path) > maxMemoryPath {
		return false
	}
	for part := range strings.SplitSeq(path, "/") {
		if !ValidMemoryScope(part) {
			return false
		}
	}
	return true
}

// checkMemory returns an error unless scope and each path are valid.
func checkMemory(scope string, paths ...string) error {
	if !ValidMemoryScope(scope) {
		return fmt.Errorf("%q is not a valid memory scope", scope)
	}
	for _, p := range paths {
		if !validMemoryPath(p) {
			return fmt.Errorf("%q is not a valid memory path", p)
		}
	}
	return nil
}

// within returns the path within the scope that the model's path names, "" for the memory directory, and whether it
// names one.
func within(path string) (string, bool) {
	path = strings.TrimSuffix(path, "/")
	if path == memoryRoot {
		return "", true
	}
	rest, found := strings.CutPrefix(path, memoryRoot+"/")
	return rest, found && validMemoryPath(rest)
}

// commandError is a memory command the tool refuses: its text is the error result the model gets.
type commandError string

func (e commandError) Error() string {
	return string(e)
}

// memoryInput is the memory tool's input; a field the call leaves out is nil.
type memoryInput struct {
	Command    string  `json:"command"`
	Path       *string `json:"path"`
	ViewRange  *[]int  `json:"view_range"`
	FileText   *string `json:"file_text"`
	OldStr     *string `json:"old_str"`
	NewStr     *string `json:"new_str"`
	InsertLine *int    `json:"insert_line"`
	InsertText *string `json:"insert_text"`
	OldPath    *string `json:"old_path"`
	NewPath    *string `json:"new_path"`
}

// runMemory runs one command in scope. Every outcome is a result, an error one when the command is refused or the
// store fails.
func runMemory(ctx context.Context, store MemoryStore, scope string, input jsontext.Value) (string, error) {
	var in memoryInput
	if err := json.Unmarshal(input, &in); err != nil {
		return "", fmt.Errorf("read the input: %w", err)
	}
	paths := []*string{in.Path}
	if in.Command == "rename" {
		paths = []*string{in.OldPath, in.NewPath}
	}
	at := make([]string, len(paths))
	for i, p := range paths {
		var ok bool
		if p != nil {
			at[i], ok = within(*p)
		}
		if !ok {
			shown := "(none)"
			if p != nil {
				shown = *p
			}
			return "", commandError("Error: The path " + shown + " is not a valid path under " + memoryRoot + ".")
		}
	}
	files, err := store.List(ctx, scope)
	if err != nil {
		return "", fmt.Errorf("list the memory: %w", err)
	}
	m := &memory{store: store, scope: scope, files: files}
	path := *paths[0]
	switch in.Command {
	case "view":
		return m.view(ctx, path, at[0], in.ViewRange)
	case "create":
		return m.create(ctx, path, at[0], in.FileText)
	case "str_replace":
		return m.replace(ctx, path, at[0], in.OldStr, in.NewStr)
	case "insert":
		return m.insert(ctx, path, at[0], in.InsertLine, in.InsertText)
	case "delete":
		return m.delete(ctx, path, at[0])
	case "rename":
		return m.rename(ctx, path, at[0], *paths[1], at[1])
	default: // The schema allows no other command; a handler called without it may get one.
		return "", commandError("Error: Unknown command " + in.Command + ".")
	}
}

// memory is a scope's files as they were when a command started; a run's writes run one at a time. Its methods
// take the path as the model wrote it, for the messages, and the path within the scope it names (at).
type memory struct {
	store MemoryStore
	scope string
	files []MemoryFile
}

func (m *memory) isFile(at string) bool {
	return slices.ContainsFunc(m.files, func(f MemoryFile) bool { return f.Path == at })
}

// under returns the files in directory at, "" being the memory directory.
func (m *memory) under(at string) []MemoryFile {
	var files []MemoryFile
	for _, f := range m.files {
		if at == "" || strings.HasPrefix(f.Path, at+"/") {
			files = append(files, f)
		}
	}
	return files
}

func (m *memory) isDir(at string) bool {
	return at == "" || len(m.under(at)) > 0
}

// fileAbove returns the first of at's directories that is a file, or "".
func (m *memory) fileAbove(at string) string {
	for i := range len(at) {
		if at[i] == '/' && m.isFile(at[:i]) {
			return at[:i]
		}
	}
	return ""
}

// read returns the text of file at, and whether there is one.
func (m *memory) read(ctx context.Context, at string) (string, bool, error) {
	if !m.isFile(at) {
		return "", false, nil
	}
	text, err := m.store.Read(ctx, m.scope, at)
	switch {
	case errors.Is(err, fs.ErrNotExist):
		return "", false, nil
	case err != nil:
		return "", false, fmt.Errorf("read the memory: %w", err)
	}
	return text, true, nil
}

func missing(path string) string {
	return "The path " + path + " does not exist. Please provide a valid path."
}

func (m *memory) view(ctx context.Context, path, at string, viewRange *[]int) (string, error) {
	text, found, err := m.read(ctx, at)
	switch {
	case err != nil:
		return "", err
	case found:
		return viewFile(path, strings.Split(strings.TrimSuffix(text, "\n"), "\n"), viewRange)
	case !m.isDir(at):
		return "", commandError(missing(path))
	}
	// Up to two levels below the directory, without hidden items, each with the size of everything in it.
	shown := memoryRoot
	if at != "" {
		shown += "/" + at
	}
	sizes := map[string]int64{}
	var total int64
	for _, f := range m.under(at) {
		total += f.Size
		parts := strings.Split(strings.TrimPrefix(f.Path, at+"/"), "/")
		for depth := 1; depth <= min(2, len(parts)) && !strings.HasPrefix(parts[depth-1], "."); depth++ {
			sizes[shown+"/"+strings.Join(parts[:depth], "/")] += f.Size
		}
	}
	var listing strings.Builder
	listing.WriteString(size(total) + "\t" + shown)
	for _, entry := range slices.Sorted(maps.Keys(sizes)) {
		listing.WriteString("\n" + size(sizes[entry]) + "\t" + entry)
	}
	return "Here're the files and directories up to 2 levels deep in " + shown + ", excluding hidden items:\n" +
		listing.String(), nil
}

// viewFile returns a file's lines, or those of viewRange, numbered; a long view ends at a line break in its second
// half, or else at the limit.
func viewFile(path string, lines []string, viewRange *[]int) (string, error) {
	from, to := 1, len(lines)
	if viewRange != nil {
		r := *viewRange
		if len(r) != 2 || r[0] < 1 || r[0] > len(lines) || (r[1] != -1 && (r[1] < r[0] || r[1] > len(lines))) {
			return "", commandError(fmt.Sprintf("Error: Invalid `view_range`: it should be [start, end] with 1 <= "+
				"start <= end <= %d, or end -1 for the end of the file.", len(lines)))
		}
		from = r[0]
		if r[1] != -1 {
			to = r[1]
		}
	}
	view := numbered(lines, from, to)
	if chars := []rune(view); len(chars) > maxMemoryView {
		end := maxMemoryView
		for i := maxMemoryView; i >= maxMemoryView/2; i-- {
			if chars[i] == '\n' {
				end = i
				break
			}
		}
		view = string(chars[:end]) + "\n[Truncated at 16000 characters: view the rest with view_range.]"
	}
	return "Here's the content of " + path + " with line numbers:\n" + view, nil
}

// numbered returns lines from to to, numbered from 1, six wide.
func numbered(lines []string, from, to int) string {
	var b strings.Builder
	for line := from; line <= to; line++ {
		if line > from {
			b.WriteByte('\n')
		}
		fmt.Fprintf(&b, "%6d\t%s", line, lines[line-1])
	}
	return b.String()
}

func (m *memory) create(ctx context.Context, path, at string, text *string) (string, error) {
	above := m.fileAbove(at)
	switch {
	case text == nil:
		return "", commandError("Error: Parameter `file_text` is required for command: create")
	case m.isDir(at):
		return "", commandError("Error: Cannot create " + path + ": it is a directory.")
	case above != "":
		return "", commandError("Error: Cannot create " + path + ": " + memoryRoot + "/" + above + " is a file.")
	}
	if err := m.write(ctx, path, at, *text); err != nil {
		return "", err
	}
	return "File created successfully at: " + path, nil
}

func (m *memory) replace(ctx context.Context, path, at string, old, replacement *string) (string, error) {
	if old == nil || *old == "" {
		return "", commandError("Error: Parameter `old_str` is required for command: str_replace")
	}
	text, exists, err := m.read(ctx, at)
	switch {
	case err != nil:
		return "", err
	case !exists:
		return "", commandError("Error: " + missing(path))
	}
	// The occurrences' lines, each once; the loop is bounded by the count, never by a search that may not end.
	var lines []string
	i := 0
	for range strings.Count(text, *old) {
		i += strings.Index(text[i:], *old)
		if line := strconv.Itoa(strings.Count(text[:i], "\n") + 1); !slices.Contains(lines, line) {
			lines = append(lines, line)
		}
		i += len(*old)
	}
	first := strings.Index(text, *old)
	switch {
	case first < 0:
		return "", commandError("No replacement was performed, old_str `" + *old + "` did not appear verbatim in " +
			path + ".")
	case strings.Count(text, *old) > 1:
		return "", commandError("No replacement was performed. Multiple occurrences of old_str `" + *old +
			"` in lines: " + strings.Join(lines, ", ") + ". Please ensure it is unique")
	}
	with := ""
	if replacement != nil {
		with = *replacement
	}
	edited := text[:first] + with + text[first+len(*old):]
	if err := m.write(ctx, path, at, edited); err != nil {
		return "", err
	}
	// The edited lines, with four lines of context on either side.
	all, line := strings.Split(edited, "\n"), strings.Count(text[:first], "\n")+1
	return "The memory file has been edited. A snippet of " + path + " with line numbers:\n" +
		numbered(all, max(1, line-4), min(len(all), line+strings.Count(with, "\n")+4)), nil
}

func (m *memory) insert(ctx context.Context, path, at string, line *int, text *string) (string, error) {
	if line == nil || text == nil {
		return "", commandError("Error: Parameters `insert_line` and `insert_text` are required for command: insert")
	}
	content, found, err := m.read(ctx, at)
	switch {
	case err != nil:
		return "", err
	case !found:
		return "", commandError("Error: The path " + path + " does not exist")
	}
	lines := strings.Split(content, "\n")
	if *line < 0 || *line > len(lines) {
		return "", commandError(fmt.Sprintf("Error: Invalid `insert_line` parameter: %d. It should be within the "+
			"range of lines of the file: [0, %d]", *line, len(lines)))
	}
	lines = slices.Insert(lines, *line, strings.TrimSuffix(*text, "\n"))
	if err := m.write(ctx, path, at, strings.Join(lines, "\n")); err != nil {
		return "", err
	}
	return "The file " + path + " has been edited.", nil
}

func (m *memory) delete(ctx context.Context, path, at string) (string, error) {
	switch {
	case at == "":
		return "", commandError("Error: The memory directory " + memoryRoot + " itself cannot be deleted.")
	case !m.isFile(at) && !m.isDir(at):
		return "", commandError("Error: The path " + path + " does not exist")
	}
	for _, f := range m.affected(at) {
		if err := m.store.Delete(ctx, m.scope, f); err != nil {
			return "", fmt.Errorf("delete from the memory: %w", err)
		}
	}
	return "Successfully deleted " + path, nil
}

// rename moves each file by writing it at its new path, then deleting it at its old one, so a failure midway loses
// nothing.
func (m *memory) rename(ctx context.Context, path, at, newPath, to string) (string, error) {
	above := m.fileAbove(to)
	switch {
	case at == "":
		return "", commandError("Error: The memory directory " + memoryRoot + " itself cannot be renamed.")
	case !m.isFile(at) && !m.isDir(at):
		return "", commandError("Error: The path " + path + " does not exist")
	case m.isFile(to) || m.isDir(to):
		return "", commandError("Error: The destination " + newPath + " already exists")
	case strings.HasPrefix(to, at+"/"):
		return "", commandError("Error: Cannot move " + path + " into itself.")
	case above != "":
		return "", commandError("Error: Cannot move to " + newPath + ": " + memoryRoot + "/" + above + " is a file.")
	}
	for _, f := range m.affected(at) {
		text, err := m.store.Read(ctx, m.scope, f)
		if err == nil {
			err = m.store.Write(ctx, m.scope, to+f[len(at):], text)
		}
		if err == nil {
			err = m.store.Delete(ctx, m.scope, f)
		}
		if err != nil {
			return "", fmt.Errorf("rename in the memory: %w", err)
		}
	}
	return "Successfully renamed " + path + " to " + newPath, nil
}

// affected returns the paths a delete or rename of at changes: the file, or every file under the directory.
func (m *memory) affected(at string) []string {
	if m.isFile(at) {
		return []string{at}
	}
	var paths []string
	for _, f := range m.under(at) {
		paths = append(paths, f.Path)
	}
	return paths
}

// write writes the file, unless its text is longer than a memory file may be.
func (m *memory) write(ctx context.Context, path, at, text string) error {
	if n := utf8.RuneCountInString(text); n > maxMemoryFile {
		return commandError(fmt.Sprintf("Error: %s would hold %d characters; a memory file holds at most %d. Keep it "+
			"shorter, or split it.", path, n, maxMemoryFile))
	}
	if err := m.store.Write(ctx, m.scope, at, text); err != nil {
		return fmt.Errorf("write to the memory: %w", err)
	}
	return nil
}

// size returns a size in bytes as a listing shows it.
func size(bytes int64) string {
	switch {
	case bytes < 1024:
		return strconv.FormatInt(bytes, 10) + "B"
	case bytes < 1024*1024:
		return strconv.FormatFloat(float64(bytes)/1024, 'f', 1, 64) + "K"
	default:
		return strconv.FormatFloat(float64(bytes)/(1024*1024), 'f', 1, 64) + "M"
	}
}
