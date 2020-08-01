.DEFAULT_GOAL 	:= default

ARTIFACTS 		:= $(shell pwd)/artifacts
BUILD			:= $(shell pwd)/.build
CONFIGURATION	:= Release
CLI_PROJECT		:= src/OneloginAwsCli/OneloginAwsCli.csproj
CLI_TOOL		:= onelogin-aws
RUNTIME 		:= $(shell uname -s | awk '{print tolower($0)}' | sed "s/darwin/osx/")-x64

.PHONY: default
default: package

.PHONY: clean
clean:
	@rm -rf $(ARTIFACTS)
	@rm -rf $(BUILD)

.PHONY: restore
restore:
	dotnet restore
	dotnet tool restore

.PHONY: restore
build: restore
	dotnet build --configuration $(CONFIGURATION) --no-restore $(CLI_PROJECT)

.PHONY: package
package: restore build
	@echo ""
	@echo "\033[0;32mPackaging nuget \033[0m"
	@echo "\033[0;32m------------------- \033[0m"

	dotnet pack $(CLI_PROJECT) --configuration $(CONFIGURATION) \
		--no-build \
		--output $(ARTIFACTS) \
		--include-symbols

.PHONY: publish-native
package-native:
	@mkdir -p $(ARTIFACTS)

	@echo ""
	@echo "\033[0;32mPackaging native \033[0m"
	@echo "\033[0;32m------------------- \033[0m"

	dotnet publish --runtime $(RUNTIME) \
		--configuration $(CONFIGURATION) \
		--self-contained true \
		--nologo \
		--output $(ARTIFACTS)/$(RUNTIME) \
		-p:PublishSingleFile=true \
		-p:PublishTrimmed=true \
		-p:IncludeNativeLibrariesInSingleFile=true