package bookshop_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"go/ast"
	"go/parser"
	"go/token"
	"slices"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
)

// The static checks that no tool takes SQL text: they need no database.

func TestTools_APP08_TheOnlyTextInputsAreKnownValues(t *testing.T) {
	t.Parallel()
	tools, err := bookshop.Tools(nil)
	if err != nil {
		t.Fatalf("Tools() error = %v", err)
	}

	var texts []string
	for _, tool := range tools {
		texts = append(texts, stringProperties(t, tool.InputSchema)...)
	}
	slices.Sort(texts)

	// Values compared as values, never run.
	want := []string{"author", "email", "genre", "name", "nameOrEmail", "title"}
	if diff := cmp.Diff(want, slices.Compact(texts)); diff != "" {
		t.Errorf("text inputs mismatch (-want +got):\n%s", diff)
	}
}

// stringProperties returns the names of the string properties of an object schema, those of its arrays' objects
// included.
func stringProperties(t *testing.T, schema jsontext.Value) []string {
	t.Helper()
	type node struct {
		Type       any                       `json:"type"`
		Items      *node                     `json:"items"`
		Properties map[string]jsontext.Value `json:"properties"`
	}
	var n node
	if err := json.Unmarshal(schema, &n); err != nil {
		t.Fatalf("schema %s: %v", schema, err)
	}
	var names []string
	for name, property := range n.Properties {
		var p node
		if err := json.Unmarshal(property, &p); err != nil {
			t.Fatalf("property %s: %v", name, err)
		}
		switch types := p.Type.(type) {
		case string:
			if types == "string" {
				names = append(names, name)
			}
		case []any:
			if slices.Contains(types, any("string")) {
				names = append(names, name)
			}
		}
		if p.Items != nil {
			items, err := json.Marshal(p.Items.Properties)
			if err != nil {
				t.Fatal(err)
			}
			names = append(names, stringProperties(t, jsontext.Value(`{"properties":`+string(items)+`}`))...)
		}
	}
	return names
}

func TestTools_APP08_EveryQueryRunsAConstant(t *testing.T) {
	t.Parallel()
	file, err := parser.ParseFile(token.NewFileSet(), "tools.go", nil, 0)
	if err != nil {
		t.Fatalf("parse tools.go: %v", err)
	}
	constants := map[string]bool{}
	for _, decl := range file.Decls {
		if gen, ok := decl.(*ast.GenDecl); ok && gen.Tok == token.CONST {
			for _, spec := range gen.Specs {
				for _, name := range spec.(*ast.ValueSpec).Names {
					constants[name.Name] = true
				}
			}
		}
	}

	// Each call that runs SQL takes the context, then the SQL: a constant, or a loop variable over constants.
	queries := 0
	ast.Inspect(file, func(n ast.Node) bool {
		c, ok := n.(*ast.CallExpr)
		if !ok {
			return true
		}
		method, ok := c.Fun.(*ast.SelectorExpr)
		if !ok || !slices.Contains([]string{"Query", "QueryRow", "Exec"}, method.Sel.Name) || len(c.Args) < 2 {
			return true
		}
		queries++
		sql, ok := c.Args[1].(*ast.Ident)
		switch {
		case !ok:
			t.Errorf("a %s call runs %T, not a constant", method.Sel.Name, c.Args[1])
		case !constants[sql.Name] && !loopOverConstants(file, sql.Name, constants):
			t.Errorf("a %s call runs %s, not a constant", method.Sel.Name, sql.Name)
		}
		return true
	})
	if queries < 15 {
		t.Errorf("found %d calls that run SQL, want at least 15", queries)
	}
}

// loopOverConstants reports whether name is the value of a range loop over a slice literal of constants.
func loopOverConstants(file *ast.File, name string, constants map[string]bool) bool {
	found := false
	ast.Inspect(file, func(n ast.Node) bool {
		loop, ok := n.(*ast.RangeStmt)
		if !ok {
			return true
		}
		value, ok := loop.Value.(*ast.Ident)
		list, isList := loop.X.(*ast.CompositeLit)
		if !ok || value.Name != name || !isList {
			return true
		}
		found = true
		for _, element := range list.Elts {
			if ident, ok := element.(*ast.Ident); !ok || !constants[ident.Name] {
				found = false
			}
		}
		return true
	})
	return found
}
