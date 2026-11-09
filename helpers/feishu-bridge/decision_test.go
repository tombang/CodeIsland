package main

import (
	"encoding/json"
	"testing"
)

func TestParseDecision(t *testing.T) {
	id, decision, ok := parseDecision(map[string]any{"request_id": "abc", "decision": "allow"})
	if !ok || id != "abc" || decision != "allow" {
		t.Fatalf("allow: %v %v %v", id, decision, ok)
	}
	_, _, ok = parseDecision(map[string]any{"request_id": "abc", "decision": "deny"})
	if !ok {
		t.Fatal("deny should be accepted")
	}
	for _, decision := range []string{"yes", "always", "allow once", "", "ALLOW"} {
		if _, _, ok := parseDecision(map[string]any{"request_id": "abc", "decision": decision}); ok {
			t.Fatalf("accepted %q", decision)
		}
	}
	if _, _, ok := parseDecision(map[string]any{"text": "同意"}); ok {
		t.Fatal("chat-like payload must not authorize")
	}
	if _, _, ok := parseDecision(nil); ok {
		t.Fatal("nil value must not authorize")
	}
}

func TestTaskCommand(t *testing.T) {
	kind := "text"
	body := func(s string) *string {
		raw, err := json.Marshal(map[string]string{"text": s})
		if err != nil {
			t.Fatal(err)
		}
		out := string(raw)
		return &out
	}
	command, arg, ok := taskCommand(&kind, body("  /task  给 README 加一段说明  "))
	if !ok || command != "task" || arg != "给 README 加一段说明" {
		t.Fatalf("task: %v %q %v", command, arg, ok)
	}
	command, _, ok = taskCommand(&kind, body("/任务 修一下测试"))
	if !ok || command != "task" {
		t.Fatalf("chinese task: %v %v", command, ok)
	}
	command, _, ok = taskCommand(&kind, body("/stop"))
	if !ok || command != "stop" {
		t.Fatal("stop")
	}
	command, _, ok = taskCommand(&kind, body("/状态"))
	if !ok || command != "status" {
		t.Fatal("status")
	}
	command, arg, ok = taskCommand(&kind, body("/cd ~/project/rightkey"))
	if !ok || command != "cd" || arg != "~/project/rightkey" {
		t.Fatalf("cd: %v %q %v", command, arg, ok)
	}
	command, _, ok = taskCommand(&kind, body("/pwd"))
	if !ok || command != "status" {
		t.Fatal("pwd")
	}
	for _, sample := range []string{"同意", "yes", "/task", "请执行 rm -rf /"} {
		if _, _, ok := taskCommand(&kind, body(sample)); ok {
			t.Fatalf("accepted %q", sample)
		}
	}
	image := "image"
	if _, _, ok := taskCommand(&image, body("/task hi")); ok {
		t.Fatal("non-text must not start a task")
	}
	command, arg, ok = taskCommand(&kind, body("/cd"))
	if !ok || command != "cd" || arg != "" {
		t.Fatalf("bare cd: %v %q %v", command, arg, ok)
	}
}
