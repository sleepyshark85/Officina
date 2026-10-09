// Package extraction is the extraction and classification sample: a stateless agent with only a model, instructions
// and typed output, which triages customer messages. Each message is one run on a new, discarded conversation, ending
// at the model's end after one call. Its tests run it offline on the test kit's scripted model.
package extraction
