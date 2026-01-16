#!/usr/bin/env bash

# Color codes
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

print_usage() {
    cat <<'USAGE'
Usage: ./.opencode/.ralph/ralph.sh [-h|--help] [-m|--max-iterations <int>]

  -h, --help              Show this message
  -m, --max-iterations    Max iterations before force stop (0 = infinite, default: 0)

The script monitors research/feature-list.json and iterates until all entries have "passes": true.
If --max-iterations > 0, the script will stop after that many iterations regardless of feature status.
USAGE
}

# Parse arguments
max_iterations=0
while [[ $# -gt 0 ]]; do
    case $1 in
        -h|--help)
            print_usage
            exit 0
            ;;
        -m|--max-iterations)
            max_iterations="$2"
            shift 2
            ;;
        *)
            echo -e "${RED}ERROR: Unknown option: $1${NC}"
            print_usage
            exit 1
            ;;
    esac
done

feature_list_path="research/feature-list.json"

# Check if feature-list.json exists in research folder
if [[ ! -f "$feature_list_path" ]]; then
    echo -e "${RED}ERROR: feature-list.json not found in research folder.${NC}"
    echo ""
    echo -e "${YELLOW}Please run the /create-feature-list command first to generate the feature list.${NC}"
    echo ""
    exit 1
fi

test_all_features_passing() {
    local path="$1"

    if [[ ! -f "$path" ]]; then
        return 1
    fi

    # Parse JSON and count features
    local total_features passing_features failing_features

    total_features=$(jq 'length' "$path" 2>/dev/null)
    if [[ $? -ne 0 || -z "$total_features" ]]; then
        echo -e "${RED}ERROR: Failed to parse research/feature-list.json${NC}"
        return 1
    fi

    if [[ "$total_features" -eq 0 ]]; then
        echo -e "${RED}ERROR: research/feature-list.json is empty.${NC}"
        return 1
    fi

    passing_features=$(jq '[.[] | select(.passes == true)] | length' "$path" 2>/dev/null)
    if [[ $? -ne 0 ]]; then
        echo -e "${RED}ERROR: Failed to parse research/feature-list.json: $?${NC}"
        return 1
    fi

    failing_features=$((total_features - passing_features))

    echo -e "${CYAN}Feature Progress: $passing_features / $total_features passing ($failing_features remaining)${NC}"

    if [[ "$failing_features" -eq 0 ]]; then
        return 0
    else
        return 1
    fi
}

echo -e "${GREEN}Starting ralph - monitoring research/feature-list.json for completion...${NC}"
if [[ "$max_iterations" -gt 0 ]]; then
    echo -e "${YELLOW}Max iterations set to $max_iterations${NC}"
fi
echo ""

iteration=0
while true; do
    ((iteration++))

    if [[ "$max_iterations" -gt 0 ]]; then
        echo "Iteration: $iteration / $max_iterations"
    else
        echo "Iteration: $iteration"
    fi

    ./.opencode/.ralph/sync.sh

    if test_all_features_passing "$feature_list_path"; then
        echo -e "${GREEN}All features passing! Exiting loop.${NC}"
        break
    fi

    if [[ "$max_iterations" -gt 0 && "$iteration" -ge "$max_iterations" ]]; then
        echo -e "${YELLOW}Max iterations ($max_iterations) reached. Force stopping.${NC}"
        break
    fi

    echo -e "===SLEEP===\n===SLEEP===\n"
    echo "looping"
    sleep 10
done