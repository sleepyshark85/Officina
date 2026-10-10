# frozen_string_literal: true

# Mutant's hooks for a gem's mutant.yml, which mutant evaluates with `hooks` in scope. While mutant inserts a mutation,
# Ruby's warnings are off: a mutated method may warn as it is parsed ("string literal in condition"), and
# WorkspaceWarnings would turn that into an error that stops the insertion, which mutant counts as a survivor though
# the tests were never run. The tests then judge the mutation, warnings on.
verbose = $VERBOSE
hooks.register(:mutation_insert_pre) { $VERBOSE = nil }
hooks.register(:mutation_insert_post) { $VERBOSE = verbose }
