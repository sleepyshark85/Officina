package officina

import (
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"sync"
	"unicode/utf8"
)

// MapMemoryStore keeps memory files in a map, for tests, demos and short-lived agents: they are gone when the
// process ends. The zero value is an empty store, safe for concurrent runs.
type MapMemoryStore struct {
	mu    sync.Mutex
	files map[[2]string]string
}

// List returns the scope's files.
func (s *MapMemoryStore) List(_ context.Context, scope string) ([]MemoryFile, error) {
	if err := checkMemory(scope); err != nil {
		return nil, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	var files []MemoryFile
	for key, text := range s.files {
		if key[0] == scope {
			files = append(files, MemoryFile{Path: key[1], Size: int64(len(text))})
		}
	}
	return files, nil
}

// Read returns the file's text.
func (s *MapMemoryStore) Read(_ context.Context, scope, path string) (string, error) {
	if err := checkMemory(scope, path); err != nil {
		return "", err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	text, found := s.files[[2]string{scope, path}]
	if !found {
		return "", fmt.Errorf("read memory file %s: %w", path, fs.ErrNotExist)
	}
	return text, nil
}

// Write creates the file or replaces its text.
func (s *MapMemoryStore) Write(_ context.Context, scope, path, text string) error {
	if err := checkMemory(scope, path); err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.files == nil {
		s.files = map[[2]string]string{}
	}
	s.files[[2]string{scope, path}] = text
	return nil
}

// Delete deletes the file.
func (s *MapMemoryStore) Delete(_ context.Context, scope, path string) error {
	if err := checkMemory(scope, path); err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.files, [2]string{scope, path})
	return nil
}

// FileMemoryStore keeps memory files on disk: each scope is a directory, named by the hex of its UTF-8 bytes so
// scopes that differ in case stay apart, of UTF-8 text files. The scope's directory must not be a link; under it,
// only a relative link that stays in the scope is followed, and an absolute one is refused even when it points
// inside, so no file outside the scope is reached; files reached only through a link are not listed. Directories left empty are removed. Create it with NewFileMemoryStore.
//
// It guards against the model's paths, not against other processes changing the directory. On a case-insensitive
// file system, paths in one scope that differ only in case name the same file.
type FileMemoryStore struct {
	dir string
}

// NewFileMemoryStore returns a store that keeps its files under dir, creating it when a file is first written.
func NewFileMemoryStore(dir string) *FileMemoryStore {
	return &FileMemoryStore{dir: dir}
}

// List returns the scope's files.
func (s *FileMemoryStore) List(_ context.Context, scope string) ([]MemoryFile, error) {
	var files []MemoryFile
	err := s.in(scope, false, func(root *os.Root) error {
		return fs.WalkDir(root.FS(), ".", func(p string, d fs.DirEntry, err error) error {
			if err != nil || !d.Type().IsRegular() || !validMemoryPath(p) {
				return err
			}
			info, err := d.Info()
			if err != nil {
				return fmt.Errorf("list memory: %w", err)
			}
			files = append(files, MemoryFile{Path: p, Size: info.Size()})
			return nil
		})
	})
	if errors.Is(err, fs.ErrNotExist) {
		return nil, nil
	}
	return files, err
}

// Read returns the file's text.
func (s *FileMemoryStore) Read(_ context.Context, scope, p string) (string, error) {
	if err := checkMemory(scope, p); err != nil {
		return "", err
	}
	var text []byte
	err := s.in(scope, false, func(root *os.Root) error {
		var err error
		if text, err = root.ReadFile(filepath.FromSlash(p)); err != nil {
			return fmt.Errorf("read memory file: %w", err)
		}
		if !utf8.Valid(text) {
			return fmt.Errorf("read memory file %s: it is not UTF-8 text", p)
		}
		return nil
	})
	return string(text), err
}

// Write creates the file or replaces its text.
func (s *FileMemoryStore) Write(_ context.Context, scope, p, text string) error {
	if err := checkMemory(scope, p); err != nil {
		return err
	}
	return s.in(scope, true, func(root *os.Root) error {
		if err := root.MkdirAll(filepath.FromSlash(path.Dir(p)), 0o700); err != nil {
			return fmt.Errorf("write memory file: %w", err)
		}
		if err := root.WriteFile(filepath.FromSlash(p), []byte(text), 0o600); err != nil {
			return fmt.Errorf("write memory file: %w", err)
		}
		return nil
	})
}

// Delete deletes the file, and the directories above it it leaves empty.
func (s *FileMemoryStore) Delete(_ context.Context, scope, p string) error {
	if err := checkMemory(scope, p); err != nil {
		return err
	}
	err := s.in(scope, false, func(root *os.Root) error {
		if err := root.Remove(filepath.FromSlash(p)); err != nil {
			return fmt.Errorf("delete memory file: %w", err)
		}
		// Removing a directory that is not empty fails, which ends the pruning.
		for dir := path.Dir(p); dir != "."; dir = path.Dir(dir) {
			if root.Remove(filepath.FromSlash(dir)) != nil {
				break
			}
		}
		return nil
	})
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	return err
}

// in runs fn on the scope's directory, created first if create is set. The directory itself must not be a link,
// and fn reaches nothing outside it.
func (s *FileMemoryStore) in(scope string, create bool, fn func(root *os.Root) error) (err error) {
	if err := checkMemory(scope); err != nil {
		return err
	}
	dir := filepath.Join(s.dir, hex.EncodeToString([]byte(scope)))
	if create {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			return fmt.Errorf("create memory scope: %w", err)
		}
	}
	info, err := os.Lstat(dir)
	switch {
	case err != nil:
		return fmt.Errorf("open memory scope: %w", err)
	case !info.IsDir():
		return fmt.Errorf("open memory scope %q: its directory is a link or not a directory", scope)
	}
	root, err := os.OpenRoot(dir)
	if err != nil {
		return fmt.Errorf("open memory scope: %w", err)
	}
	defer func() {
		if closeErr := root.Close(); err == nil && closeErr != nil {
			err = fmt.Errorf("close memory scope: %w", closeErr)
		}
	}()
	return fn(root)
}
